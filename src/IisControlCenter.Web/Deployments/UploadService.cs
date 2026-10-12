using System.Security.Cryptography;
using System.Text.Json;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Deployments;

public sealed record UploadStart(Guid SessionId, int ChunkSize, int TotalChunks);

public sealed record UploadInfo(
    Guid SessionId, UploadStatus Status, int TotalChunks, int ChunkSize, IReadOnlyList<int> Received, string? Error,
    IReadOnlyList<ZipIssue> Errors, Guid? PackageId, IReadOnlyList<string> Warnings, string? AvText);

/// <summary>
/// Chunked upload (not Blazor's file upload, which streams over the SignalR connection). The browser sends fixed-size chunks over plain HTTP;
/// the portal assembles, validates, scans and stores the package. Interrupted uploads can be resumed chunk by chunk.
/// </summary>
public sealed class UploadService(
    IDbContextFactory<AppDbContext> dbf,
    IPackageStore store,
    IPortalAntivirus antivirus,
    IOptions<DeploymentOptions> options,
    AuditService audit,
    TimeProvider clock)
{
    private DeploymentOptions O => options.Value;
    private static readonly JsonSerializerOptions Json = new();

    // ---------------- start ----------------

    public async Task<OpResult<UploadStart>> StartAsync(Actor actor, Guid componentId, string environment, string fileName, long totalSize)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var comp = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId);
        if (comp is null) return OpResult<UploadStart>.Fail("Component not found.");
        if (!actor.Access.CanOnProject(ProjectPermission.SubmitDeployment, comp.ProjectId, environment))
        {
            await audit.WriteAsync(actor, new AuditRecord("Package.Upload", "Component", componentId.ToString(), EnvironmentName: environment, Result: "Denied"));
            throw new ForbiddenException("You do not hold the Deployer role for this project and environment.");
        }
        fileName = Path.GetFileName(fileName ?? "");
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return OpResult<UploadStart>.Fail("Only .zip files can be uploaded.");
        if (fileName.Length > 250) return OpResult<UploadStart>.Fail("The file name is too long.");
        if (totalSize <= 0) return OpResult<UploadStart>.Fail("The file is empty.");
        if (totalSize > O.MaxPackageBytes) return OpResult<UploadStart>.Fail($"The file is larger than the limit of {O.MaxPackageMb} MB.");

        var chunk = O.ChunkBytes;
        var s = new UploadSession
        {
            Id = Guid.NewGuid(), UserId = actor.UserId, UserName = actor.UserName, ComponentId = componentId, FileName = fileName, TotalSize = totalSize,
            ChunkSize = chunk, TotalChunks = (int)((totalSize + chunk - 1) / chunk), Status = UploadStatus.Receiving,
            CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddHours(O.UploadSessionHours),
        };
        db.UploadSessions.Add(s);
        await db.SaveChangesAsync();
        Directory.CreateDirectory(Dir(s.Id));
        return OpResult<UploadStart>.Success(new UploadStart(s.Id, s.ChunkSize, s.TotalChunks));
    }

    // ---------------- chunks ----------------

    public async Task<OpResult<int>> ReceiveChunkAsync(Actor actor, Guid sessionId, int index, Stream body, string? sha256Hex, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || !Owns(actor, s)) return OpResult<int>.Fail("Upload not found.");
        if (s.Status != UploadStatus.Receiving) return OpResult<int>.Fail($"This upload is {s.Status}.");
        if (s.ExpiresAt < clock.GetUtcNow()) return OpResult<int>.Fail("This upload has expired. Start again.");
        if (index < 0 || index >= s.TotalChunks) return OpResult<int>.Fail("Chunk number out of range.");

        var expected = index < s.TotalChunks - 1 ? s.ChunkSize : s.TotalSize - (long)(s.TotalChunks - 1) * s.ChunkSize;
        var tmp = Path.Combine(Dir(sessionId), $"{index:D6}.tmp");
        long written = 0;
        using var sha = SHA256.Create();
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buf = new byte[81920]; int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                written += n;
                if (written > expected) { fs.Close(); File.Delete(tmp); return OpResult<int>.Fail("The chunk is larger than expected."); }
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        if (written != expected) { File.Delete(tmp); return OpResult<int>.Fail($"The chunk has {written} bytes but {expected} were expected."); }
        if (!string.IsNullOrWhiteSpace(sha256Hex) && !string.Equals(sha256Hex.Trim(), Convert.ToHexString(sha.Hash!), StringComparison.OrdinalIgnoreCase))
        { File.Delete(tmp); return OpResult<int>.Fail("The chunk was damaged in transit (hash mismatch). Send it again."); }

        File.Move(tmp, Path.Combine(Dir(sessionId), $"{index:D6}.part"), overwrite: true);
        return OpResult<int>.Success(Received(sessionId).Count);
    }

    public async Task<OpResult> CompleteAsync(Actor actor, Guid sessionId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.UploadSessions.FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || !Owns(actor, s)) return OpResult.Fail("Upload not found.");
        if (s.Status != UploadStatus.Receiving) return OpResult.Fail($"This upload is {s.Status}.");
        var got = Received(sessionId);
        if (got.Count != s.TotalChunks)
            return OpResult.Fail($"{s.TotalChunks - got.Count} chunk(s) are missing: " + string.Join(", ", Enumerable.Range(0, s.TotalChunks).Except(got).Take(10)));
        s.Status = UploadStatus.Assembling;
        await db.SaveChangesAsync();
        return OpResult.Success();
    }

    public async Task<UploadInfo?> GetAsync(Actor actor, Guid sessionId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.UploadSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || !Owns(actor, s)) return null;
        var errors = string.IsNullOrEmpty(s.ErrorsJson) ? [] : JsonSerializer.Deserialize<List<ZipIssue>>(s.ErrorsJson) ?? [];
        var warnings = new List<string>(); string? av = null;
        if (s.PackageId is { } pid)
        {
            var p = await db.Packages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid);
            if (p is not null) { av = $"{p.AvResult}{(p.AvDetail is null ? "" : ": " + p.AvDetail)}"; if (p.WarningsJson is not null) warnings = JsonSerializer.Deserialize<List<string>>(p.WarningsJson) ?? []; }
        }
        return new UploadInfo(s.Id, s.Status, s.TotalChunks, s.ChunkSize, Directory.Exists(Dir(s.Id)) ? Received(s.Id) : [], s.Error, errors, s.PackageId, warnings, av);
    }

    // ---------------- background processing ----------------

    /// <summary>Assembles, validates and scans every upload that is waiting. Safe to call often.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var ids = await db.UploadSessions.AsNoTracking().Where(s => s.Status == UploadStatus.Assembling).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in ids) await ProcessAsync(id, ct);
        return ids.Count;
    }

    private async Task ProcessAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var s = await db.UploadSessions.FirstAsync(x => x.Id == sessionId, ct);
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == s.ComponentId, ct);
        var assembled = Path.Combine(Dir(sessionId), "assembled.zip");

        async Task Reject(string error, IEnumerable<ZipIssue>? issues = null)
        {
            s.Status = UploadStatus.Rejected; s.Error = error;
            s.ErrorsJson = issues is null ? null : JsonSerializer.Serialize(issues.Take(100), Json);
            await db.SaveChangesAsync(ct);
            TryDeleteDir(sessionId);
            await audit.WriteAsync(s.UserId, s.UserName, null, new AuditRecord("Package.Rejected", "Component", s.ComponentId.ToString(), After: $"{s.FileName}: {error}", Result: "Failed",
                Detail: issues is null ? null : string.Join("; ", issues.Take(5).Select(i => $"{i.Path}: {i.Message}"))));
        }

        try
        {
            // 1. assemble and hash
            string sha;
            long size = 0;
            using (var h = SHA256.Create())
            await using (var outFs = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                for (var i = 0; i < s.TotalChunks; i++)
                {
                    await using var part = new FileStream(Path.Combine(Dir(sessionId), $"{i:D6}.part"), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    var buf = new byte[81920]; int n;
                    while ((n = await part.ReadAsync(buf, ct)) > 0) { await outFs.WriteAsync(buf.AsMemory(0, n), ct); h.TransformBlock(buf, 0, n, null, 0); size += n; }
                }
                h.TransformFinalBlock([], 0, 0);
                sha = Convert.ToHexString(h.Hash!);
            }
            foreach (var f in Directory.GetFiles(Dir(sessionId), "*.part")) File.Delete(f);
            if (size != s.TotalSize) { await Reject($"The assembled file has {size} bytes but {s.TotalSize} were announced."); return; }

            // 2. validate content
            s.Status = UploadStatus.Validating; await db.SaveChangesAsync(ct);
            var extra = (comp.AllowedFileTypes ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var policy = new ZipPolicy(MaxEntries: O.MaxEntries, MaxUncompressedBytes: O.MaxUncompressedMb * 1024L * 1024, AllowedExtraExtensions: extra);
            ZipValidation v;
            await using (var zs = new FileStream(assembled, FileMode.Open, FileAccess.Read, FileShare.Read)) v = ZipValidator.Validate(zs, policy, ct);
            if (!v.IsValid) { await Reject($"The package was refused: {v.Errors.Count} problem(s).", v.Errors); return; }

            // 3. antivirus
            var av = AvResult.ScanOnTarget; string? avDetail = null;
            if (!O.AntivirusMode.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            {
                var r = await antivirus.ScanAsync(assembled, ct);
                if (!File.Exists(assembled)) r = new ScanResult(ScanOutcome.Infected, "The scanner removed the file.");     // Defender may quarantine it
                switch (r.Outcome)
                {
                    case ScanOutcome.Clean: av = AvResult.Clean; avDetail = r.Detail; break;
                    case ScanOutcome.Infected: await Reject("The antivirus found a threat: " + r.Detail); return;
                    case ScanOutcome.Unavailable when O.AntivirusMode.Equals("Auto", StringComparison.OrdinalIgnoreCase):
                        av = AvResult.ScanOnTarget; avDetail = "The portal could not scan (" + r.Detail + "). Each target server scans the package with its own antivirus before deploying."; break;
                    default: await Reject("The package could not be scanned: " + r.Detail); return;
                }
            }
            else avDetail = "Scanned on each target server before deployment.";

            // 4. store
            var now = clock.GetUtcNow();
            var pkg = new Package
            {
                Id = Guid.NewGuid(), ComponentId = s.ComponentId, FileName = s.FileName, Size = size, Sha256 = sha, AvResult = av, AvDetail = avDetail, AvScannedAt = now,
                EntryCount = v.Files.Count, UncompressedSize = v.TotalUncompressedBytes, WarningsJson = v.Warnings.Count == 0 ? null : JsonSerializer.Serialize(v.Warnings, Json),
                UploadedBy = s.UserName, UploadedById = s.UserId, UploadedAt = now, ExpiresAt = now.AddDays(O.RetentionDays),
            };
            pkg.StoragePath = store.Add(pkg.Id, assembled, v.Files);
            db.Packages.Add(pkg);
            s.Status = UploadStatus.Ready; s.PackageId = pkg.Id; s.Error = null; s.ErrorsJson = null;
            await db.SaveChangesAsync(ct);
            TryDeleteDir(sessionId);
            await audit.WriteAsync(s.UserId, s.UserName, null, new AuditRecord("Package.Upload", "Package", pkg.Id.ToString(),
                After: $"{s.FileName}; {size:N0} bytes; sha256={sha[..16]}…; {v.Files.Count} files; antivirus={av}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await Reject("The package could not be processed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------------- housekeeping ----------------

    /// <summary>Removes the working files of finished or expired uploads and marks old unfinished ones Expired.</summary>
    public async Task<int> CleanupAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var open = await db.UploadSessions.Where(s => (s.Status == UploadStatus.Receiving) && s.ExpiresAt < now).ToListAsync();
        foreach (var s in open) { s.Status = UploadStatus.Expired; TryDeleteDir(s.Id); }
        await db.SaveChangesAsync();
        // Orphan folders (for example after a crash).
        var known = (await db.UploadSessions.AsNoTracking().Where(s => s.Status == UploadStatus.Receiving || s.Status == UploadStatus.Assembling || s.Status == UploadStatus.Validating).Select(s => s.Id).ToListAsync()).Select(i => i.ToString("N")).ToHashSet();
        var removed = 0;
        foreach (var d in Directory.GetDirectories(store.UploadTempRoot))
            if (!known.Contains(Path.GetFileName(d)) && Directory.GetCreationTimeUtc(d) < now.UtcDateTime.AddHours(-1)) { try { Directory.Delete(d, true); removed++; } catch (IOException) { } }
        return open.Count + removed;
    }

    // ---------------- helpers ----------------

    private string Dir(Guid id) => Path.Combine(store.UploadTempRoot, id.ToString("N"));
    private static bool Owns(Actor a, UploadSession s) => string.Equals(a.UserId, s.UserId, StringComparison.OrdinalIgnoreCase);
    private List<int> Received(Guid id) =>
        Directory.GetFiles(Dir(id), "*.part").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f))).OrderBy(i => i).ToList();
    private void TryDeleteDir(Guid id) { try { if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true); } catch (IOException) { } }
}
