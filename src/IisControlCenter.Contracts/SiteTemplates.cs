namespace IisControlCenter.Contracts;

public sealed record TemplateApp(string Path, string Folder);
public sealed record TemplateVDir(string AppPath, string Path, string Folder);
public sealed record TemplateAccess(string Folder, FolderAccess Level);

/// <summary>
/// A reusable layout for a new site: pool settings, folders, applications, virtual directories and folder access.
/// All folders are RELATIVE to the site folder chosen when the template is applied ("" = the site folder itself).
/// A template has no host names, no server names and no passwords.
/// </summary>
public sealed record SiteTemplateDef
{
    public PoolConfig? Pool { get; init; }
    public IReadOnlyList<string> Folders { get; init; } = [];
    public IReadOnlyList<TemplateApp> Apps { get; init; } = [];
    public IReadOnlyList<TemplateVDir> VirtualDirectories { get; init; } = [];
    public IReadOnlyList<TemplateAccess> Access { get; init; } = [];
    public bool HasModify => Access.Any(a => a.Level == FolderAccess.Modify);
}

public static class TemplateRules
{
    public const int MaxItems = 50;

    /// <summary>A relative folder: "logs", "data/uploads", or "" for the site folder. No rooted paths, drive letters, "..", odd characters.</summary>
    public static string? ValidateRelativeFolder(string? f, bool allowEmpty)
    {
        f = (f ?? "").Trim().Replace('\\', '/').Trim('/');
        if (f.Length == 0) return allowEmpty ? null : "Enter a folder.";
        if (f.Length > 200) return "The folder is too long.";
        foreach (var seg in f.Split('/'))
        {
            if (seg is "" or "." or "..") return $"'{f}' is not a valid relative folder.";
            if (seg.Any(c => char.IsControl(c) || "<>:\"|?*\\".Contains(c)) || seg.EndsWith('.') || seg.EndsWith(' ')) return $"'{seg}' has characters that are not allowed.";
        }
        return null;
    }

    public static string NormalizeFolder(string f) => (f ?? "").Trim().Replace('\\', '/').Trim('/');

    public static List<string> Validate(SiteTemplateDef d)
    {
        var e = new List<string>();
        if (d.Pool is { IsEmpty: false } p) e.AddRange(PoolRules.Validate(p));
        if (d.Folders.Count + d.Apps.Count + d.VirtualDirectories.Count + d.Access.Count > MaxItems) e.Add($"A template can hold at most {MaxItems} items.");
        foreach (var f in d.Folders) if (ValidateRelativeFolder(f, false) is { } m) e.Add(m);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in d.Apps)
        {
            if (IisRules.ValidateVirtualPath(IisRules.NormalizeVirtualPath(a.Path), "application") is { } m) e.Add(m);
            if (ValidateRelativeFolder(a.Folder, false) is { } m2) e.Add(m2);
            if (!seen.Add(IisRules.NormalizeVirtualPath(a.Path))) e.Add($"The application {a.Path} is listed twice.");
        }
        foreach (var v in d.VirtualDirectories)
        {
            if (IisRules.ValidateVirtualPath(IisRules.NormalizeVirtualPath(v.Path), "virtual directory") is { } m) e.Add(m);
            if (ValidateRelativeFolder(v.Folder, false) is { } m2) e.Add(m2);
            var app = string.IsNullOrWhiteSpace(v.AppPath) ? "/" : IisRules.NormalizeVirtualPath(v.AppPath);
            if (app != "/" && !d.Apps.Any(a => string.Equals(IisRules.NormalizeVirtualPath(a.Path), app, StringComparison.OrdinalIgnoreCase))) e.Add($"The virtual directory {v.Path} is in {app}, which is not an application of this template.");
            if (app != "/" && !IisRules.NormalizeVirtualPath(v.Path).StartsWith(app + "/", StringComparison.OrdinalIgnoreCase)) e.Add($"The virtual directory {v.Path} must be inside {app}.");
            if (!seen.Add(IisRules.NormalizeVirtualPath(v.Path))) e.Add($"{v.Path} is listed twice.");
        }
        foreach (var a in d.Access) if (ValidateRelativeFolder(a.Folder, true) is { } m) e.Add(m);
        return e.Distinct().ToList();
    }

    public static string? ValidateName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "Enter a template name.";
        if (n.Trim().Length > 80) return "The template name is too long.";
        return null;
    }
}

public sealed record TemplateApply(string SiteName, string RootFolder, string PoolName, IReadOnlyList<HttpBinding> Bindings);

public static class TemplatePlanner
{
    /// <summary>The marker for "the site created earlier in this same batch".</summary>
    public const long NewSite = IisOpCommand.NewSiteMarker;

    private static string Join(string root, string rel)
    {
        var sep = root.Contains('/') && !root.Contains('\\') ? "/" : "\\";
        var r = TemplateRules.NormalizeFolder(rel);
        return r.Length == 0 ? root.TrimEnd('\\', '/') : root.TrimEnd('\\', '/') + sep + r.Replace("/", sep);
    }

    /// <summary>
    /// The exact commands, in order: pool, site (creates the root folder, starts stopped), extra folders, applications, virtual directories, folder access.
    /// Nothing is guessed: the same plan is shown to the approver in Production.
    /// </summary>
    public static List<IisOpCommand> Plan(SiteTemplateDef def, TemplateApply a)
    {
        var list = new List<IisOpCommand>
        {
            new() { Op = IisOp.CreatePool, PoolName = a.PoolName, PoolConfig = def.Pool is { IsEmpty: false } ? def.Pool : null },
            new() { Op = IisOp.CreateSite, SiteName = a.SiteName, PhysicalPath = a.RootFolder, CreateFolder = true, PoolName = a.PoolName, Bindings = a.Bindings },
        };
        foreach (var f in def.Folders.Select(TemplateRules.NormalizeFolder).Distinct(StringComparer.OrdinalIgnoreCase))
            list.Add(new() { Op = IisOp.CreateFolder, PhysicalPath = Join(a.RootFolder, f) });
        foreach (var app in def.Apps)
            list.Add(new() { Op = IisOp.CreateApplication, SiteId = NewSite, AppPath = IisRules.NormalizeVirtualPath(app.Path), PhysicalPath = Join(a.RootFolder, app.Folder), CreateFolder = true, PoolName = a.PoolName });
        foreach (var v in def.VirtualDirectories)
            list.Add(new() { Op = IisOp.CreateVirtualDirectory, SiteId = NewSite, AppPath = string.IsNullOrWhiteSpace(v.AppPath) ? "/" : IisRules.NormalizeVirtualPath(v.AppPath), VirtualPath = IisRules.NormalizeVirtualPath(v.Path), PhysicalPath = Join(a.RootFolder, v.Folder), CreateFolder = true });
        foreach (var acl in def.Access)
        {
            var target = Join(a.RootFolder, acl.Folder);
            // the folder must exist: it is the site folder, or created by a step above
            list.Add(new() { Op = IisOp.GrantFolderAccess, PoolName = a.PoolName, PhysicalPath = target, Access = acl.Level });
        }
        return list;
    }
}
