using System.Security.Cryptography;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Agent.Deploy;

/// <summary>Reads a target folder. Never follows links out of the folder and never looks inside protected data folders.</summary>
public static class FolderScanner
{
    public static string Sha256File(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static LiveFile? Probe(string root, string relative, bool hash)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return null;
        if (IsReparsePoint(full)) return new LiveFile(relative, new FileInfo(full).Length, "LINK");   // a link never equals package content
        var info = new FileInfo(full);
        return new LiveFile(relative, info.Length, hash ? Sha256File(full) : null);
    }

    /// <summary>Every regular file under root outside protected data folders. Relative paths use '/'.</summary>
    public static List<LiveFile> All(string root, ProtectedPaths protectedPaths, bool hash, int limit = 500_000)
    {
        var result = new List<LiveFile>();
        Walk(root, "", protectedPaths, hash, result, limit);
        return result;
    }

    private static void Walk(string root, string rel, ProtectedPaths pp, bool hash, List<LiveFile> result, int limit)
    {
        var dir = rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            var r = rel.Length == 0 ? name : rel + "/" + name;
            if (pp.IsInDataFolder(r)) continue;
            if (IsReparsePoint(file)) continue;
            if (result.Count >= limit) throw new InvalidOperationException($"The folder has more than {limit} files.");
            result.Add(new LiveFile(r, new FileInfo(file).Length, hash ? Sha256File(file) : null));
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            var r = rel.Length == 0 ? name : rel + "/" + name;
            if (pp.IsDataFolderPath(r)) continue;          // never enter a data folder
            if (IsReparsePoint(sub)) continue;             // never follow a junction or symlink
            Walk(root, r, pp, hash, result, limit);
        }
    }

    /// <summary>Total size of the files a backup would contain.</summary>
    public static long TotalSize(string root, ProtectedPaths pp) => All(root, pp, hash: false).Sum(f => f.Size);
}
