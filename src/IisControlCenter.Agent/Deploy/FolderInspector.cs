using System.Text;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Agent.Deploy;

/// <summary>Read-only answers about a target folder for the approver's diff. Returns hashes and sizes; contents only for one masked config file.</summary>
public sealed class FolderInspector(TargetValidator validator, ProtectedStore protectedStore)
{
    public const int MaxPaths = 200_000;
    public const long MaxMaskedFileBytes = 1024 * 1024;
    private static readonly string[] TextExtensions = [".config", ".json", ".xml", ".ini", ".yml", ".yaml", ".properties", ".txt", ".conf", ".settings"];

    public ManifestResult GetManifest(Guid commandId, ManifestRequest req, IReadOnlyList<string> paths)
    {
        try
        {
            validator.Validate(req.PhysicalPath, req.IisSiteId, req.AppPath);
            var target = Path.GetFullPath(req.PhysicalPath);
            // The union of what the portal says and what this agent has always protected: the portal cannot shrink it.
            var pp = protectedStore.Merge(target, req.DataFolders, []);
            if (paths.Count > MaxPaths) return Fail(commandId, "Too many paths requested.");

            var existing = new List<ManifestEntry>();
            foreach (var raw in paths)
            {
                var p = RelativePaths.Normalize(raw, out _);
                if (p is null || pp.IsInDataFolder(p)) continue;                       // data folders are never read
                var l = FolderScanner.Probe(target, p, hash: true);
                if (l is not null) existing.Add(new ManifestEntry(l.Path, l.Size, l.Sha256));
            }
            var all = new List<ManifestEntry>();
            var total = 0;
            if (req.ListAll)
            {
                var files = FolderScanner.All(target, pp, hash: false);
                total = files.Count;
                all.AddRange(files.Select(f => new ManifestEntry(f.Path, f.Size, null)));
            }
            return new ManifestResult(commandId, true, null, existing, all, total);
        }
        catch (StepFailedException ex) { return Fail(commandId, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Fail(commandId, ex.Message); }
    }

    public MaskedFileResult GetMaskedFile(Guid commandId, MaskedFileRequest req)
    {
        try
        {
            validator.Validate(req.PhysicalPath, req.IisSiteId, req.AppPath);
            var target = Path.GetFullPath(req.PhysicalPath);
            var rel = RelativePaths.Normalize(req.RelativePath, out var err) ?? throw new StepFailedException(Steps.Validate, "Invalid file path: " + err);
            var pp = protectedStore.Merge(target, [], req.ConfigPatterns);

            // Only a protected CONFIG file, only a text-like type, only inside the target, and never through a link.
            if (!pp.IsConfigFile(rel)) return new(commandId, false, "That file is not a protected config file.", false, false, null);
            if (pp.IsInDataFolder(rel)) return new(commandId, false, "That file is inside a protected data folder.", false, false, null);
            if (!TextExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant())) return new(commandId, false, "Only text-like config files can be shown.", false, false, null);
            var full = Path.GetFullPath(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar)));
            DeploymentExecutor.EnsureSafeParent(target, full);
            if (!File.Exists(full)) return new(commandId, true, null, false, false, null);
            var info = new FileInfo(full);
            if (FolderScanner.IsReparsePoint(full)) return new(commandId, false, "That file is a link.", true, false, null);
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(info.Length, MaxMaskedFileBytes);
            var buf = new byte[len];
            var read = fs.Read(buf, 0, len);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            return new(commandId, true, null, true, info.Length > MaxMaskedFileBytes, SecretMasker.Mask(text, req.Salt));   // masked HERE: the secret never leaves this server
        }
        catch (StepFailedException ex) { return new(commandId, false, ex.Message, false, false, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(commandId, false, ex.Message, false, false, null); }
    }

    private static ManifestResult Fail(Guid id, string error) => new(id, false, error, [], [], 0);
}
