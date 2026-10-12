namespace IisControlCenter.Core.Deployments;

public enum DiffKind { Same, Added, Removed }
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>Small line diff for config files (already masked before it gets here).</summary>
public static class TextDiff
{
    public const int MaxLines = 3000;

    /// <summary>Returns null when the inputs are too large to compare.</summary>
    public static IReadOnlyList<DiffLine>? Diff(string before, string after)
    {
        var a = Split(before); var b = Split(after);
        if (a.Length > MaxLines || b.Length > MaxLines) return null;

        var m = a.Length; var n = b.Length;
        var lcs = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
            for (var j = n - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < m && y < n)
        {
            if (a[x] == b[y]) { result.Add(new(DiffKind.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add(new(DiffKind.Removed, a[x++]));
            else result.Add(new(DiffKind.Added, b[y++]));
        }
        while (x < m) result.Add(new(DiffKind.Removed, a[x++]));
        while (y < n) result.Add(new(DiffKind.Added, b[y++]));
        return result;
    }

    /// <summary>Keeps changed lines and <paramref name="context"/> lines around them; marks skipped runs with a "…" line.</summary>
    public static IReadOnlyList<DiffLine> Collapse(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Kind != DiffKind.Same)
                for (var k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++) keep[k] = true;
        var result = new List<DiffLine>();
        var skipping = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i]) { result.Add(lines[i]); skipping = false; }
            else if (!skipping) { result.Add(new DiffLine(DiffKind.Same, "…")); skipping = true; }
        }
        return result;
    }

    private static string[] Split(string s) => (s ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
