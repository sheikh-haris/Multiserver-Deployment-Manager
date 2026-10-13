namespace IisControlCenter.Contracts.Deployments;

/// <summary>
/// In IIS an application or virtual directory often lives in a sub-folder of its parent site. That is normal and allowed.
/// The rule that keeps it safe: while deploying to the PARENT, everything that IIS serves from a folder inside it
/// (other applications, virtual directories) is treated as a protected data folder. It is never overwritten, never deleted
/// and never part of the backup. The child is deployed by its own request.
/// </summary>
public static class NestedFolders
{
    private static string Norm(string? p) => (p ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Folders strictly inside <paramref name="targetPath"/>, relative, in data-folder form ("sub/dir/").
    /// Same-folder entries and entries elsewhere are ignored.
    /// </summary>
    public static IReadOnlyList<string> RelativeInside(string targetPath, IEnumerable<string?> otherPaths)
    {
        var root = Norm(targetPath);
        if (root.Length == 0) return [];
        var result = new List<string>();
        foreach (var o in otherPaths)
        {
            var p = Norm(o);
            if (p.Length <= root.Length + 1) continue;
            if (!p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = p[(root.Length + 1)..];
            if (rel.Split('/').Any(seg => seg is "" or "." or "..")) continue;
            result.Add(rel + "/");
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
