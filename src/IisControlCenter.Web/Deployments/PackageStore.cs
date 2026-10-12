using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Domain;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Deployments;

public interface IPackageStore
{
    string UploadTempRoot { get; }
    /// <summary>Moves an assembled, validated zip into the store and writes its manifest. Returns the storage path (relative to the store).</summary>
    string Add(Guid packageId, string assembledZip, IReadOnlyList<PackageFile> manifest);
    IReadOnlyList<PackageFile> ReadManifest(Package p);
    FileStream OpenRead(Package p);
    string? ReadEntryText(Package p, string relativePath, int maxBytes, out bool truncated);
    void Delete(Package p);
    bool Exists(Package p);
    void WriteChanges(Guid requestId, Guid targetId, IReadOnlyList<PlannedChange> changes);
    IReadOnlyList<PlannedChange> ReadChanges(Guid requestId, Guid targetId);
    void DeleteChanges(Guid requestId);
}

/// <summary>Plain files on a local folder (decision D8). Sits outside any web root; nothing here is ever served directly.</summary>
public sealed class FilePackageStore : IPackageStore
{
    private readonly string _root;
    public string UploadTempRoot { get; }
    private static readonly JsonSerializerOptions Json = new();

    public FilePackageStore(IOptions<DeploymentOptions> options, IHostEnvironment env)
    {
        var path = options.Value.PackageStorePath;
        _root = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
        Directory.CreateDirectory(_root);
        UploadTempRoot = Path.Combine(_root, "_uploads");
        Directory.CreateDirectory(UploadTempRoot);
        Directory.CreateDirectory(Path.Combine(_root, "_changes"));
    }

    public FilePackageStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        UploadTempRoot = Path.Combine(_root, "_uploads");
        Directory.CreateDirectory(UploadTempRoot);
        Directory.CreateDirectory(Path.Combine(_root, "_changes"));
    }

    private string Full(Package p)
    {
        // Paths in the database are relative to the store root. Refuse anything that resolves elsewhere.
        var full = Path.GetFullPath(Path.Combine(_root, p.StoragePath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Package path is outside the store.");
        return full;
    }

    public string Add(Guid packageId, string assembledZip, IReadOnlyList<PackageFile> manifest)
    {
        var rel = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), packageId.ToString("N") + ".zip");
        var dest = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(assembledZip, dest, overwrite: false);
        File.WriteAllText(dest + ".manifest.json", JsonSerializer.Serialize(manifest, Json));
        return rel;
    }

    public IReadOnlyList<PackageFile> ReadManifest(Package p) =>
        JsonSerializer.Deserialize<List<PackageFile>>(File.ReadAllText(Full(p) + ".manifest.json")) ?? [];

    public FileStream OpenRead(Package p) => new(Full(p), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public string? ReadEntryText(Package p, string relativePath, int maxBytes, out bool truncated)
    {
        truncated = false;
        using var zip = ZipFile.OpenRead(Full(p));
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var s = entry.Open();
        var buf = new byte[Math.Min(entry.Length, maxBytes)];
        var read = 0;
        while (read < buf.Length) { var n = s.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
        truncated = entry.Length > maxBytes;
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    public bool Exists(Package p) { try { return File.Exists(Full(p)); } catch (InvalidOperationException) { return false; } }

    public void Delete(Package p)
    {
        var full = Full(p);
        if (File.Exists(full)) File.Delete(full);
        if (File.Exists(full + ".manifest.json")) File.Delete(full + ".manifest.json");
    }

    private string ChangesFile(Guid requestId, Guid targetId) => Path.Combine(_root, "_changes", requestId.ToString("N"), targetId.ToString("N") + ".json");

    public void WriteChanges(Guid requestId, Guid targetId, IReadOnlyList<PlannedChange> changes)
    {
        var f = ChangesFile(requestId, targetId);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, JsonSerializer.Serialize(changes, Json));
    }

    public IReadOnlyList<PlannedChange> ReadChanges(Guid requestId, Guid targetId)
    {
        var f = ChangesFile(requestId, targetId);
        return File.Exists(f) ? JsonSerializer.Deserialize<List<PlannedChange>>(File.ReadAllText(f)) ?? [] : [];
    }

    public void DeleteChanges(Guid requestId)
    {
        var d = Path.Combine(_root, "_changes", requestId.ToString("N"));
        if (Directory.Exists(d)) Directory.Delete(d, true);
    }
}
