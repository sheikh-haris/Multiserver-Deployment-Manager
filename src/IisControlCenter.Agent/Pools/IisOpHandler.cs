using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Deploy;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging;

namespace IisControlCenter.Agent.Pools;

/// <summary>
/// Create sites, applications, virtual directories, pools and folders, start or stop a site, replace HTTP bindings, grant an app pool account access to a folder.
/// Every rule is checked here, whatever the portal says: envelope, the LOCAL Managed switch, the allowed roots, name and binding rules,
/// conflicts with what already exists in IIS. There is no delete.
/// </summary>
public sealed class IisOpHandler(CommandPolicy policy, AgentConfig config, IIisAdmin iis, ILogger<IisOpHandler> log)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);          // ServerManager commits collide otherwise

    private static IisOpResult Fail(Guid id, string error) => new(id, false, error, null, null, false);

    public async Task<IisOpResult> RunAsync(CommandEnvelope e, IisOpCommand c)
    {
        if (policy.Validate(e) is { } why) return Fail(e.CommandId, why);
        if (policy.EffectiveMode(e.Mode) != ServerMode.Managed)
            return Fail(e.CommandId, "This server is in ReadOnly mode. IIS can only be changed on a server that is Managed in the portal AND was installed with Managed mode allowed.");
        await OneAtATime.WaitAsync();
        try
        {
            _requireNew = c.RequireNewFolder;
            var r = await Task.Run(() => Execute(e.CommandId, c));
            log.LogInformation("IIS operation {Op}: {Result}. {After}", c.Op, r.Ok ? "ok" : "refused: " + r.Error, r.After);
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IIS operation {Op} failed", c.Op);
            return Fail(e.CommandId, "IIS refused the change: " + ex.Message);
        }
        finally { OneAtATime.Release(); }
    }

    private IisOpResult Execute(Guid id, IisOpCommand c) => c.Op switch
    {
        IisOp.CreatePool => CreatePool(id, c),
        IisOp.CreateSite => CreateSite(id, c),
        IisOp.StartSite => SetRunning(id, c, true),
        IisOp.StopSite => SetRunning(id, c, false),
        IisOp.UpdateBindings => UpdateBindings(id, c),
        IisOp.CreateApplication => CreateApp(id, c),
        IisOp.CreateVirtualDirectory => CreateVDir(id, c),
        IisOp.CreateFolder => CreateFolder(id, c),
        IisOp.GrantFolderAccess => Grant(id, c),
        _ => Fail(id, "Unknown operation."),
    };

    // ---------------- helpers ----------------

    private string? SafeNewPath(string? path, out string normalized)
    {
        normalized = PathGuard.Normalize(path) ?? "";
        if (normalized.Length == 0) return "The folder is not a valid local path (no .., UNC, drive letter only, or odd names).";
        if (!PathGuard.IsStrictlyUnderAnyRoot(path, config.AllowedRoots)) return $"The folder must be inside an allowed root of this server ({string.Join("; ", config.AllowedRoots)}), not the root itself.";
        return null;
    }

    /// <summary>No link (junction/symlink) anywhere in the part of the path that exists, so a path inside a root cannot lead outside it.</summary>
    private string? NoLinksOnPath(string path)
    {
        var full = Path.GetFullPath(path);
        var cur = full;
        while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur)) cur = Path.GetDirectoryName(cur);
        while (!string.IsNullOrEmpty(cur))
        {
            var norm = PathGuard.Normalize(cur);
            if (norm is null || !PathGuard.IsUnderAnyRoot(cur, config.AllowedRoots)) break;                 // above the roots: not our business
            if (Directory.Exists(cur) && FolderScanner.IsReparsePoint(cur)) return $"'{cur}' is a link. Links inside the roots are not followed.";
            cur = Path.GetDirectoryName(cur);
        }
        return null;
    }

    private bool _requireNew;

    private string? EnsureFolder(string path, bool create)
    {
        if (_requireNew && Directory.Exists(path)) return "The folder already exists. It must be a new folder.";
        var bad = NoLinksOnPath(path);
        if (bad is not null) return bad;
        if (Directory.Exists(path)) return null;
        if (!create) return $"The folder '{path}' does not exist. Tick 'create the folder' or create it first.";
        Directory.CreateDirectory(path);
        return null;
    }

    private string? FolderInUse(string normalized, long exceptSiteId = 0)
    {
        foreach (var s in iis.Sites())
            foreach (var a in s.Apps)
                foreach (var v in a.VirtualDirs)
                    if (PathGuard.Normalize(v.PhysicalPath) == normalized)
                        return $"The folder is already used by IIS ({s.Name}{(a.Path == "/" ? "" : a.Path)}{(v.Path == "/" ? "" : v.Path)}). Two items must not share one folder.";
        return null;
    }

    private string? BindingConflicts(IReadOnlyList<HttpBinding> wanted, long exceptSiteId)
    {
        var taken = iis.Sites().Where(s => s.Id != exceptSiteId).SelectMany(s => s.HttpBindings.Select(b => (Key: IisRules.Key(b), s.Name))).ToList();
        foreach (var b in wanted)
            if (taken.FirstOrDefault(t => t.Key == IisRules.Key(b)) is { Name: not null } hit)
                return $"{b.Host}:{b.Port} is already bound by the site '{hit.Name}'.";
        return null;
    }

    private static string Describe(IReadOnlyList<HttpBinding> b) => b.Count == 0 ? "(none)" : string.Join(", ", b.OrderBy(x => IisRules.Key(x)).Select(x => $"http://{x.Host}:{x.Port}"));

    // ---------------- operations ----------------

    private IisOpResult CreatePool(Guid id, IisOpCommand c)
    {
        if (PoolRules.ValidateName(c.PoolName) is { } n) return Fail(id, n);
        if (c.PoolConfig is { IsEmpty: false } cfg && PoolRules.Validate(cfg) is { Count: > 0 } p) return Fail(id, string.Join(" ", p));
        if (iis.PoolExists(c.PoolName!)) return Fail(id, $"App pool '{c.PoolName}' already exists.");
        iis.CreatePool(c.PoolName!, c.PoolConfig);
        return new(id, true, null, "(did not exist)", $"app pool {c.PoolName}", true);
    }

    private IisOpResult CreateSite(Guid id, IisOpCommand c)
    {
        if (IisRules.ValidateSiteName(c.SiteName) is { } sn) return Fail(id, sn);
        var bp = IisRules.ValidateBindings(c.Bindings); if (bp.Count > 0) return Fail(id, string.Join(" ", bp));
        if (string.IsNullOrWhiteSpace(c.PoolName) || !iis.PoolExists(c.PoolName)) return Fail(id, $"App pool '{c.PoolName}' does not exist on this server. Create it first.");
        if (iis.Sites().Any(s => string.Equals(s.Name, c.SiteName, StringComparison.OrdinalIgnoreCase))) return Fail(id, $"A site named '{c.SiteName}' already exists.");
        if (SafeNewPath(c.PhysicalPath, out var norm) is { } pe) return Fail(id, pe);
        if (FolderInUse(norm) is { } used) return Fail(id, used);
        if (BindingConflicts(c.Bindings!, 0) is { } bc) return Fail(id, bc);
        if (EnsureFolder(c.PhysicalPath!, c.CreateFolder) is { } fe) return Fail(id, fe);
        var siteId = iis.CreateSite(c.SiteName!, c.PhysicalPath!, c.PoolName!, c.Bindings!);
        return new(id, true, null, "(did not exist)", $"site {c.SiteName} (stopped); folder {c.PhysicalPath}; pool {c.PoolName}; {Describe(c.Bindings!)}", true, siteId);
    }

    private IisOpResult SetRunning(Guid id, IisOpCommand c, bool run)
    {
        var s = iis.Sites().FirstOrDefault(x => x.Id == c.SiteId);
        if (s is null) return Fail(id, "The site does not exist on this server.");
        var after = iis.SetSiteRunning(c.SiteId, run);
        return new(id, true, null, s.State, after, s.State != after, c.SiteId);
    }

    private IisOpResult UpdateBindings(Guid id, IisOpCommand c)
    {
        var s = iis.Sites().FirstOrDefault(x => x.Id == c.SiteId);
        if (s is null) return Fail(id, "The site does not exist on this server.");
        var bp = IisRules.ValidateBindings(c.Bindings); if (bp.Count > 0) return Fail(id, string.Join(" ", bp));
        var live = s.HttpBindings.Select(IisRules.Key).OrderBy(x => x).ToList();
        var saw = (c.ExpectedBindings ?? []).Select(IisRules.Key).OrderBy(x => x).ToList();
        if (!live.SequenceEqual(saw)) return Fail(id, $"The bindings were changed by someone else since you opened them (now: {Describe(s.HttpBindings)}). Nothing was changed. Reload and try again.");
        var want = c.Bindings!.Select(IisRules.Key).OrderBy(x => x).ToList();
        if (live.SequenceEqual(want)) return new(id, true, null, Describe(s.HttpBindings), Describe(s.HttpBindings), false, c.SiteId);
        if (BindingConflicts(c.Bindings!, c.SiteId) is { } bc) return Fail(id, bc);
        iis.SetHttpBindings(c.SiteId, c.Bindings!);
        var after = iis.Sites().First(x => x.Id == c.SiteId);
        if (!after.HttpBindings.Select(IisRules.Key).OrderBy(x => x).SequenceEqual(want)) return Fail(id, "IIS accepted the change but the site does not show the new bindings. Check IIS Manager.");
        return new(id, true, null, Describe(s.HttpBindings), Describe(after.HttpBindings), true, c.SiteId);
    }

    private IisOpResult CreateApp(Guid id, IisOpCommand c)
    {
        var s = iis.Sites().FirstOrDefault(x => x.Id == c.SiteId);
        if (s is null) return Fail(id, "The site does not exist on this server.");
        var path = IisRules.NormalizeVirtualPath(c.AppPath ?? "");
        if (IisRules.ValidateVirtualPath(path, "application") is { } pe) return Fail(id, pe);
        if (s.Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) return Fail(id, $"The application {path} already exists on {s.Name}.");
        if (s.Apps.SelectMany(a => a.VirtualDirs).Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase))) return Fail(id, $"{path} is already a virtual directory.");
        if (string.IsNullOrWhiteSpace(c.PoolName) || !iis.PoolExists(c.PoolName)) return Fail(id, $"App pool '{c.PoolName}' does not exist on this server.");
        if (SafeNewPath(c.PhysicalPath, out var norm) is { } fe0) return Fail(id, fe0);
        if (FolderInUse(norm) is { } used) return Fail(id, used);
        if (EnsureFolder(c.PhysicalPath!, c.CreateFolder) is { } fe) return Fail(id, fe);
        iis.CreateApplication(c.SiteId, path, c.PhysicalPath!, c.PoolName!);
        return new(id, true, null, "(did not exist)", $"application {s.Name}{path}; folder {c.PhysicalPath}; pool {c.PoolName}", true, c.SiteId);
    }

    private IisOpResult CreateVDir(Guid id, IisOpCommand c)
    {
        var s = iis.Sites().FirstOrDefault(x => x.Id == c.SiteId);
        if (s is null) return Fail(id, "The site does not exist on this server.");
        var app = IisRules.NormalizeVirtualPath(string.IsNullOrWhiteSpace(c.AppPath) ? "/" : c.AppPath);
        var path = IisRules.NormalizeVirtualPath(c.VirtualPath ?? "");
        if (IisRules.ValidateVirtualPath(path, "virtual directory") is { } pe) return Fail(id, pe);
        var owner = s.Apps.FirstOrDefault(a => string.Equals(a.Path, app, StringComparison.OrdinalIgnoreCase));
        if (owner is null) return Fail(id, $"The application {app} does not exist on {s.Name}.");
        if (app != "/" && !path.StartsWith(app + "/", StringComparison.OrdinalIgnoreCase)) return Fail(id, $"The virtual directory must be inside {app} (for example {app}/files).");
        if (s.Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) || s.Apps.SelectMany(a => a.VirtualDirs).Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)))
            return Fail(id, $"{path} already exists on {s.Name}.");
        if (SafeNewPath(c.PhysicalPath, out var norm) is { } fe0) return Fail(id, fe0);
        if (FolderInUse(norm) is { } used) return Fail(id, used);
        if (EnsureFolder(c.PhysicalPath!, c.CreateFolder) is { } fe) return Fail(id, fe);
        iis.CreateVirtualDirectory(c.SiteId, app, path, c.PhysicalPath!);
        return new(id, true, null, "(did not exist)", $"virtual directory {s.Name}{path} → {c.PhysicalPath}", true, c.SiteId);
    }

    private IisOpResult CreateFolder(Guid id, IisOpCommand c)
    {
        if (SafeNewPath(c.PhysicalPath, out _) is { } pe) return Fail(id, pe);
        var existed = Directory.Exists(c.PhysicalPath!);
        if (EnsureFolder(c.PhysicalPath!, true) is { } fe) return Fail(id, fe);
        return new(id, true, null, existed ? "exists" : "(did not exist)", c.PhysicalPath, !existed);
    }

    private IisOpResult Grant(Guid id, IisOpCommand c)
    {
        if (IisRules.ValidateAccess(c.Access) is { } ae) return Fail(id, ae);
        if (SafeNewPath(c.PhysicalPath, out _) is { } pe) return Fail(id, pe);
        if (PoolRules.ValidateName(c.PoolName) is { } n) return Fail(id, n);
        if (!iis.PoolExists(c.PoolName!)) return Fail(id, $"App pool '{c.PoolName}' does not exist on this server. The access can only be given to the account of an existing pool.");
        if (!Directory.Exists(c.PhysicalPath!)) return Fail(id, "The folder does not exist.");
        if (NoLinksOnPath(c.PhysicalPath!) is { } link) return Fail(id, link);
        var now = iis.GetAccess(c.PhysicalPath!, c.PoolName!);
        string D(FolderAccess? a) => a is null ? "no access" : a.ToString()!;
        if (now is { } have && have >= c.Access) return new(id, true, null, D(now), D(now), false);
        iis.GrantAccess(c.PhysicalPath!, c.PoolName!, c.Access!.Value);
        var after = iis.GetAccess(c.PhysicalPath!, c.PoolName!);
        if (after is null || after < c.Access) return Fail(id, "The permission was written but does not show up. Check the folder's Security tab.");
        return new(id, true, null, D(now), $"{D(after)} for IIS AppPool\\{c.PoolName} on {c.PhysicalPath}", true);
    }
}
