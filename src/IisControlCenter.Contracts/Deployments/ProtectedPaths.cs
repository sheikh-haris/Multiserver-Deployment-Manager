using System.Text.RegularExpressions;

namespace IisControlCenter.Contracts.Deployments;

/// <summary>
/// The two protected lists of a component.
/// Config files (for example web.config, appsettings*.json) are copied only when they do not exist yet.
/// Data folders (for example uploads\) are never overwritten, never deleted and never backed up.
/// A pattern without a slash matches in the ROOT of the target only. Use "**/" to match at any depth.
/// </summary>
public sealed class ProtectedPaths
{
    private readonly List<Regex> _config;
    private readonly List<string> _dataRoots;
    private readonly List<Regex> _dataAnywhere;

    public IReadOnlyList<string> ConfigPatterns { get; }
    public IReadOnlyList<string> DataFolders { get; }

    public ProtectedPaths(IEnumerable<string> configPatterns, IEnumerable<string> dataFolders)
    {
        ConfigPatterns = configPatterns.Select(Clean).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DataFolders = dataFolders.Select(x => Clean(x).TrimEnd('/')).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _config = ConfigPatterns.Select(ToRegex).ToList();
        _dataRoots = DataFolders.Where(d => !d.StartsWith("**/")).ToList();
        _dataAnywhere = DataFolders.Where(d => d.StartsWith("**/")).Select(d => ToRegex(d + "/**")).ToList();
    }

    public static ProtectedPaths Defaults { get; } =
        new(["web.config", "appsettings*.json"], ["uploads", "generated-files"]);

    private static string Clean(string s) => (s ?? "").Trim().Replace('\\', '/').TrimStart('/');

    public bool IsConfigFile(string relativePath) => _config.Any(r => r.IsMatch(relativePath));

    public bool IsInDataFolder(string relativePath)
    {
        foreach (var d in _dataRoots)
            if (relativePath.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return _dataAnywhere.Any(r => r.IsMatch(relativePath));
    }

    /// <summary>True for a directory (relative path) that is one of the data folders or inside one.</summary>
    public bool IsDataFolderPath(string relativeDir) => IsInDataFolder(relativeDir.TrimEnd('/'));

    private static Regex ToRegex(string pattern)
    {
        var p = Regex.Escape(pattern).Replace(@"\*\*/", "(?:.*/)?").Replace(@"/\*\*", "(?:/.*)?").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return new Regex("^" + p + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
