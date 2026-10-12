namespace IisControlCenter.Agent.Security;

/// <summary>
/// Decides whether a path is inside an allowed root. Pure string logic with Windows rules
/// (case-insensitive, both slashes) so it behaves the same everywhere and can be unit tested anywhere.
/// This is the check that must hold even if the portal is compromised.
/// </summary>
public static class PathGuard
{
    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static readonly string[] ForbiddenRoots =
        [@"\windows", @"\program files", @"\program files (x86)", @"\programdata", @"\users", @"\system volume information"];

    /// <summary>Returns a normalised path like "d:\websites\a", or null when the path is unsafe or not absolute.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
#if DEBUG
        // Development only: allow POSIX paths so the portal and agent can be tried on Linux or macOS. Not compiled into Release builds.
        if (Environment.GetEnvironmentVariable("ICC_DEV_POSIX_PATHS") == "1" && path.StartsWith('/') && !path.Contains("..") && !path.Contains('\\') && !path.Contains('\0') && path == path.Trim())
            return path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/');
#endif
        if (path != path.Trim()) return null;                       // leading or trailing spaces: refuse instead of guessing
        var p = path.Replace('/', '\\');
        if (p.Contains('\0')) return null;
        if (p.StartsWith(@"\\")) return null;                       // UNC and \\?\ device paths are not allowed
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;   // must be "X:\..."
        if (p.IndexOf(':', 2) >= 0) return null;                    // alternate data streams or a second colon

        var parts = new List<string>();
        foreach (var seg in p[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") return null;                           // never resolve upward: reject
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return null; // Windows trims these, which can hide the real target
            if (seg.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return null;
            var stem = seg.Split('.')[0].ToUpperInvariant();
            if (Reserved.Contains(stem)) return null;
            parts.Add(seg);
        }
        return (char.ToLowerInvariant(p[0]) + @":\" + string.Join('\\', parts)).ToLowerInvariant().TrimEnd('\\') is { } s
            ? (s.Length == 2 ? s + @"\" : s)
            : null;
    }

    /// <summary>Null when the root is acceptable for a config file; otherwise the reason.</summary>
    public static string? ValidateRoot(string root)
    {
        var n = Normalize(root);
        if (n is null) return "must be an absolute local path such as D:\\Websites (no .., UNC or odd names)";
        if (n.Length <= 3) return "a whole drive is not allowed";
        var afterDrive = n[2..];
        if (ForbiddenRoots.Any(f => afterDrive == f || afterDrive.StartsWith(f + "\\"))) return "system folders are not allowed";
        return null;
    }

    /// <summary>True when <paramref name="path"/> equals or is below one of the roots.</summary>
    public static bool IsUnderAnyRoot(string? path, IEnumerable<string> roots)
    {
        var n = Normalize(path);
        if (n is null) return false;
        foreach (var r in roots)
        {
            var root = Normalize(r);
            if (root is null || ValidateRoot(r) is not null) continue;
            var sep = n.StartsWith('/') ? "/" : "\\";        // '/' only exists in Development builds (ICC_DEV_POSIX_PATHS)
            if (n == root || n.StartsWith(root + sep, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
