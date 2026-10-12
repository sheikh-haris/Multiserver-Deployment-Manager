using System.IO.Compression;
using System.Text.Json;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Agent.Deploy;

public sealed record BackupInfo(string BackupId, string TargetPath, DateTimeOffset CreatedAt, Guid RunId, long SizeBytes);

/// <summary>
/// Backups of a target folder as timestamped zips, excluding protected data folders (so a restore never touches user data).
/// The agent only restores backups it made itself, listed in its own index.
/// </summary>
public sealed class BackupStore(AgentDirs dirs)
{
    private readonly Lock _gate = new();
    private string IndexFile => Path.Combine(dirs.Backups, "index.json");

    public string PathOf(string backupId) => Path.Combine(dirs.Backups, backupId + ".zip");

    public BackupInfo Create(string targetRoot, ProtectedPaths pp, Guid runId, DateTimeOffset now, CancellationToken ct)
    {
        var id = $"{now.UtcDateTime:yyyyMMddHHmmss}-{runId.ToString("N")[..8]}";
        var zipPath = PathOf(id);
        var tmp = zipPath + ".partial";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in FolderScanner.All(targetRoot, pp, hash: false))
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Path.Combine(targetRoot, f.Path.Replace('/', Path.DirectorySeparatorChar));
                    var entry = zip.CreateEntry(f.Path, CompressionLevel.Fastest);
                    using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }

        var info = new BackupInfo(id, targetRoot, now, runId, new FileInfo(zipPath).Length);
        lock (_gate) { var all = ReadIndex(); all.Add(info); WriteIndex(all); }
        return info;
    }

    public BackupInfo? Find(string backupId)
    {
        lock (_gate) return ReadIndex().FirstOrDefault(b => b.BackupId == backupId && File.Exists(PathOf(b.BackupId)));
    }

    public IReadOnlyList<BackupInfo> ForTarget(string targetPath)
    {
        lock (_gate) return ReadIndex().Where(b => string.Equals(Norm(b.TargetPath), Norm(targetPath), StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>Keeps the newest <paramref name="keep"/> backups of this folder (at least one) and deletes the rest. Returns how many were removed.</summary>
    public int Prune(string targetPath, int keep)
    {
        keep = Math.Max(1, keep);
        lock (_gate)
        {
            var all = ReadIndex();
            var mine = all.Where(b => string.Equals(Norm(b.TargetPath), Norm(targetPath), StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.CreatedAt).ToList();
            var drop = mine.Skip(keep).ToList();
            foreach (var b in drop) { try { File.Delete(PathOf(b.BackupId)); } catch (IOException) { } }
            WriteIndex(all.Where(b => !drop.Contains(b)).ToList());
            return drop.Count;
        }
    }

    /// <summary>
    /// Makes the folder match the backup: files that are not in the backup are removed (except protected data),
    /// everything in the backup is written back. Safe to run again if it was interrupted.
    /// </summary>
    public void Restore(string backupId, string targetRoot, ProtectedPaths pp, Action<string>? log, CancellationToken ct)
    {
        var zipPath = PathOf(backupId);
        using var zip = ZipFile.OpenRead(zipPath);
        var inBackup = new HashSet<string>(RelativePaths.Comparer);
        foreach (var e in zip.Entries)
        {
            var p = RelativePaths.Normalize(e.FullName, out _);
            if (p is not null && !e.FullName.EndsWith('/')) inBackup.Add(p);
        }

        var removed = 0;
        foreach (var f in FolderScanner.All(targetRoot, pp, hash: false))
        {
            if (inBackup.Contains(f.Path)) continue;
            if (f.Path.Equals(DeploymentExecutor.OfflineFileName, StringComparison.OrdinalIgnoreCase)) continue;
            ct.ThrowIfCancellationRequested();
            DeploymentExecutor.DeleteFile(Path.Combine(targetRoot, f.Path.Replace('/', Path.DirectorySeparatorChar)));
            removed++;
        }
        DeploymentExecutor.PruneEmptyDirectories(targetRoot, pp);

        var written = 0; var unchanged = 0;
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.FullName.EndsWith('/')) continue;
            var p = RelativePaths.Normalize(e.FullName, out _);
            if (p is null || pp.IsInDataFolder(p)) continue;
            var dest = Path.Combine(targetRoot, p.Replace('/', Path.DirectorySeparatorChar));
            DeploymentExecutor.EnsureSafeParent(targetRoot, dest);
            // A file that already has the backup's content is left alone. This also means a file that is locked by the
            // running application and was never changed does not make the restore fail.
            if (IsIdentical(dest, e)) { unchanged++; continue; }
            DeploymentExecutor.WriteFromStream(dest, e);
            written++;
        }
        log?.Invoke($"Restored {written} files ({unchanged} already identical) and removed {removed} files that were not in the backup.");
    }

    private static bool IsIdentical(string dest, ZipArchiveEntry e)
    {
        try
        {
            var info = new FileInfo(dest);
            if (!info.Exists || info.Length != e.Length || FolderScanner.IsReparsePoint(dest)) return false;
            using var a = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = e.Open();
            var bufA = new byte[81920]; var bufB = new byte[81920];
            while (true)
            {
                var na = ReadFully(a, bufA); var nb = ReadFully(b, bufB);
                if (na != nb || !bufA.AsSpan(0, na).SequenceEqual(bufB.AsSpan(0, nb))) return false;
                if (na == 0) return true;
            }
        }
        catch (IOException) { return false; }      // cannot be read (locked): let the normal write try
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        var total = 0;
        while (total < buf.Length) { var n = s.Read(buf, total, buf.Length - total); if (n == 0) break; total += n; }
        return total;
    }

    private static string Norm(string p) => p.Trim().TrimEnd('\\', '/');

    private List<BackupInfo> ReadIndex()
    {
        if (!File.Exists(IndexFile)) return [];
        try { return JsonSerializer.Deserialize<List<BackupInfo>>(File.ReadAllText(IndexFile)) ?? []; }
        catch (JsonException) { return []; }
    }

    private void WriteIndex(List<BackupInfo> all)
    {
        var tmp = IndexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all));
        File.Move(tmp, IndexFile, overwrite: true);
    }
}
