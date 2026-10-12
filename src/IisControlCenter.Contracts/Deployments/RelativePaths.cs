using System.Text.RegularExpressions;

namespace IisControlCenter.Contracts.Deployments;

/// <summary>
/// Rules for a path INSIDE a package or a target folder. Shared by the portal (upload validation, diff)
/// and the agent (extraction), so both always agree. Result paths use '/' and keep their original case.
/// </summary>
public static class RelativePaths
{
    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public const int MaxSegmentLength = 255;
    public const int MaxTotalLength = 1000;

    /// <summary>Returns null and sets <paramref name="error"/> when the name is not a safe relative file path.</summary>
    public static string? Normalize(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(name)) { error = "empty name"; return null; }
        if (name.Length > MaxTotalLength) { error = "path too long"; return null; }
        foreach (var ch in name)
            if (ch < 32 || ch == 127) { error = "control character in name"; return null; }

        var p = name.Replace('\\', '/');
        if (p.StartsWith('/')) { error = "absolute path"; return null; }
        if (p.Contains(':')) { error = "drive letter, stream or colon in name"; return null; }   // C:\x, file.txt:stream
        if (Regex.IsMatch(p, @"[<>""|?*]")) { error = "invalid character in name"; return null; }

        var parts = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { error = "'..' in path"; return null; }
            if (seg.Length > MaxSegmentLength) { error = "name segment too long"; return null; }
            if (seg.EndsWith('.') || seg.EndsWith(' ')) { error = "name ends with a dot or space"; return null; }
            if (seg.StartsWith(' ')) { error = "name starts with a space"; return null; }
            if (Reserved.Contains(seg.Split('.')[0].ToUpperInvariant())) { error = $"reserved Windows name '{seg}'"; return null; }
            parts.Add(seg);
        }
        if (parts.Count == 0) { error = "empty path"; return null; }
        return string.Join('/', parts);
    }

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
