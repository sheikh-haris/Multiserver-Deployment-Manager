using System.Text.RegularExpressions;

namespace IisControlCenter.Contracts;

/// <summary>What an IIS management command does. There is no delete: removing things stays in IIS Manager (decision for Phase 4b).</summary>
public enum IisOp
{
    CreatePool,
    CreateSite,
    StartSite,
    StopSite,
    UpdateBindings,
    CreateApplication,
    CreateVirtualDirectory,
    CreateFolder,
    GrantFolderAccess,
}

public sealed record HttpBinding(string Host, int Port);

public enum FolderAccess { Read, ReadExecute, Modify }

/// <summary>
/// One command with the fields of the chosen operation. The agent only looks at the fields that belong to <see cref="Op"/>.
/// <see cref="Expected"/> is for stale checks (for example the bindings the person saw).
/// </summary>
public sealed record IisOpCommand
{
    public IisOp Op { get; init; }
    public string? PoolName { get; init; }
    public PoolConfig? PoolConfig { get; init; }
    public string? SiteName { get; init; }
    public long SiteId { get; init; }
    public string? PhysicalPath { get; init; }
    /// <summary>Create the folder if it does not exist (inside the allowed roots only).</summary>
    public bool CreateFolder { get; init; }
    /// <summary>Refuse if the folder already exists. Set for project Owners, so a new site can never expose a folder that was already there.</summary>
    public bool RequireNewFolder { get; init; }
    public IReadOnlyList<HttpBinding>? Bindings { get; init; }
    public IReadOnlyList<HttpBinding>? ExpectedBindings { get; init; }
    public string? AppPath { get; init; }
    public string? VirtualPath { get; init; }
    public FolderAccess? Access { get; init; }
}

/// <summary>Human-readable before and after lines (for the audit log), plus ids of what was created.</summary>
public sealed record IisOpResult(Guid CommandId, bool Ok, string? Error, string? Before, string? After, bool Changed, long? SiteId = null);

/// <summary>Shared by the portal (to refuse early) and the agent (to refuse for real).</summary>
public static class IisRules
{
    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SegmentRx = new(@"^[A-Za-z0-9][A-Za-z0-9._ -]{0,62}$", RegexOptions.CultureInvariant);

    public const int MinPort = 80;
    public const int MaxPort = 65535;
    public const int MaxBindings = 20;

    public static string? ValidateSiteName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "Enter a site name.";
        if (n.Length > 64) return "The site name is too long (max 64).";
        if (n != n.Trim() || n.Any(c => char.IsControl(c) || "\\/:*?\"<>|,;".Contains(c))) return "The site name has characters that are not allowed.";
        return null;
    }

    /// <summary>A host name is required: a binding without one would answer for every name on that port.</summary>
    public static string? ValidateBinding(HttpBinding b)
    {
        if (string.IsNullOrWhiteSpace(b.Host)) return "Every binding needs a host name (for example app.example.local).";
        if (b.Host.Contains('*') || !HostRx.IsMatch(b.Host.Trim())) return $"'{b.Host}' is not a valid host name. Wildcards are not allowed.";
        if (b.Port is < MinPort or > MaxPort) return $"Port {b.Port} is out of range ({MinPort} to {MaxPort}).";
        if (b.Port == 443) return "Port 443 is for HTTPS. HTTPS bindings are managed in IIS Manager.";
        return null;
    }

    public static List<string> ValidateBindings(IReadOnlyList<HttpBinding>? list, bool allowEmpty = false)
    {
        var e = new List<string>();
        if (list is null || list.Count == 0) { if (!allowEmpty) e.Add("Add at least one binding."); return e; }
        if (list.Count > MaxBindings) e.Add($"At most {MaxBindings} bindings.");
        foreach (var b in list) if (ValidateBinding(b) is { } m) e.Add(m);
        var keys = list.Select(Key).ToList();
        if (keys.Distinct().Count() != keys.Count) e.Add("A binding is listed twice.");
        return e;
    }

    public static string Key(HttpBinding b) => $"{b.Host.Trim().ToLowerInvariant()}:{b.Port}";

    /// <summary>"/api" or "/api/v2". Each part is a plain name. Not "/" (that is the root).</summary>
    public static string? ValidateVirtualPath(string? p, string what)
    {
        if (string.IsNullOrWhiteSpace(p) || !p.StartsWith('/') || p == "/") return $"The {what} path must look like /api.";
        if (p.Length > 200 || p.EndsWith('/')) return $"The {what} path is not valid.";
        foreach (var seg in p[1..].Split('/'))
            if (!SegmentRx.IsMatch(seg) || seg.EndsWith(' ') || seg.EndsWith('.')) return $"'{seg}' is not allowed in a {what} path.";
        return null;
    }

    public static string NormalizeVirtualPath(string p) => p.Trim().Replace('\\', '/');

    public static string? ValidateAccess(FolderAccess? a) => a is null ? "Choose the kind of access." : null;
}
