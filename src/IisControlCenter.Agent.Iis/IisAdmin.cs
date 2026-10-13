using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using IisControlCenter.Contracts;
using Microsoft.Web.Administration;

namespace IisControlCenter.Agent.Iis;

public sealed record AppInfo(string Path, string PoolName, string? PhysicalPath, IReadOnlyList<(string Path, string? PhysicalPath)> VirtualDirs);
public sealed record SiteInfo(long Id, string Name, string State, IReadOnlyList<HttpBinding> HttpBindings, IReadOnlyList<AppInfo> Apps);

/// <summary>
/// The IIS changes the portal may ask for. Deliberately small: create things, start or stop a site, replace the HTTP bindings of a site,
/// give an app pool account access to a folder. There is no delete and no HTTPS binding. The handler checks every rule before calling these.
/// </summary>
public interface IIisAdmin
{
    IReadOnlyList<SiteInfo> Sites();
    bool PoolExists(string name);
    void CreatePool(string name, PoolConfig? config);
    /// <summary>Creates the site STOPPED. Returns its IIS id.</summary>
    long CreateSite(string name, string physicalPath, string pool, IReadOnlyList<HttpBinding> bindings);
    string SetSiteRunning(long siteId, bool running);
    void SetHttpBindings(long siteId, IReadOnlyList<HttpBinding> bindings);
    void CreateApplication(long siteId, string appPath, string physicalPath, string pool);
    void CreateVirtualDirectory(long siteId, string appPath, string virtualPath, string physicalPath);
    /// <summary>The access the pool account has on the folder now (null = none by an explicit rule).</summary>
    FolderAccess? GetAccess(string folder, string pool);
    void GrantAccess(string folder, string pool, FolderAccess access);
}

[SupportedOSPlatform("windows")]
public sealed class IisAdmin : IIisAdmin
{
    private static string Exp(string? p) => string.IsNullOrEmpty(p) ? "" : Environment.ExpandEnvironmentVariables(p);
    private static string State(Site s) { try { return s.State.ToString(); } catch { return "Unknown"; } }

    private static List<HttpBinding> Http(Site s) => s.Bindings.Where(b => b.Protocol == "http")
        .Select(b => new HttpBinding(b.Host ?? "", b.EndPoint?.Port ?? 0)).ToList();

    public IReadOnlyList<SiteInfo> Sites()
    {
        using var mgr = new ServerManager();
        return mgr.Sites.Select(s => new SiteInfo(s.Id, s.Name, State(s), Http(s),
            s.Applications.Select(a => new AppInfo(a.Path, a.ApplicationPoolName, Exp(a.VirtualDirectories.FirstOrDefault(v => v.Path == "/")?.PhysicalPath),
                a.VirtualDirectories.Select(v => (v.Path, (string?)Exp(v.PhysicalPath))).ToList())).ToList())).ToList();
    }

    public bool PoolExists(string name) { using var mgr = new ServerManager(); return mgr.ApplicationPools[name] is not null; }

    public void CreatePool(string name, PoolConfig? config)
    {
        using var mgr = new ServerManager();
        mgr.ApplicationPools.Add(name);
        mgr.CommitChanges();
        if (config is { IsEmpty: false }) new IisPoolAdmin().Apply(name, config);
    }

    public long CreateSite(string name, string physicalPath, string pool, IReadOnlyList<HttpBinding> bindings)
    {
        using var mgr = new ServerManager();
        var first = bindings[0];
        var site = mgr.Sites.Add(name, "http", $"*:{first.Port}:{first.Host}", physicalPath);
        foreach (var b in bindings.Skip(1)) site.Bindings.Add($"*:{b.Port}:{b.Host}", "http");
        site.ServerAutoStart = false;                                   // a new site must not take traffic before someone looks at it
        site.Applications["/"].ApplicationPoolName = pool;
        mgr.CommitChanges();
        var id = mgr.Sites[name].Id;
        try { var s = mgr.Sites[name]; if (s.State is ObjectState.Started or ObjectState.Starting) { s.Stop(); } } catch { /* already stopped */ }
        return id;
    }

    public string SetSiteRunning(long siteId, bool running)
    {
        using var mgr = new ServerManager();
        var site = mgr.Sites.FirstOrDefault(s => s.Id == siteId) ?? throw new InvalidOperationException("Site not found.");
        if (running) { if (site.State is ObjectState.Stopped or ObjectState.Stopping) site.Start(); }
        else if (site.State is ObjectState.Started or ObjectState.Starting) site.Stop();
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            var st = mgr.Sites.First(s => s.Id == siteId).State;
            if (st is ObjectState.Started or ObjectState.Stopped) return st.ToString();
        }
        return State(mgr.Sites.First(s => s.Id == siteId));
    }

    public void SetHttpBindings(long siteId, IReadOnlyList<HttpBinding> bindings)
    {
        using var mgr = new ServerManager();
        var site = mgr.Sites.FirstOrDefault(s => s.Id == siteId) ?? throw new InvalidOperationException("Site not found.");
        foreach (var b in site.Bindings.Where(b => b.Protocol == "http").ToList()) site.Bindings.Remove(b);       // HTTPS bindings stay as they are
        foreach (var b in bindings) site.Bindings.Add($"*:{b.Port}:{b.Host}", "http");
        mgr.CommitChanges();
    }

    public void CreateApplication(long siteId, string appPath, string physicalPath, string pool)
    {
        using var mgr = new ServerManager();
        var site = mgr.Sites.FirstOrDefault(s => s.Id == siteId) ?? throw new InvalidOperationException("Site not found.");
        var app = site.Applications.Add(appPath, physicalPath);
        app.ApplicationPoolName = pool;
        mgr.CommitChanges();
    }

    public void CreateVirtualDirectory(long siteId, string appPath, string virtualPath, string physicalPath)
    {
        using var mgr = new ServerManager();
        var site = mgr.Sites.FirstOrDefault(s => s.Id == siteId) ?? throw new InvalidOperationException("Site not found.");
        var app = site.Applications.FirstOrDefault(a => string.Equals(a.Path, appPath, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Application not found.");
        // IIS wants the path relative to the application.
        var rel = appPath == "/" ? virtualPath : virtualPath[appPath.Length..];
        app.VirtualDirectories.Add(rel, physicalPath);
        mgr.CommitChanges();
    }

    private static FileSystemRights Rights(FolderAccess a) => a switch
    {
        FolderAccess.Read => FileSystemRights.Read,
        FolderAccess.ReadExecute => FileSystemRights.ReadAndExecute,
        _ => FileSystemRights.Modify,
    };

    private static NTAccount Account(string pool) => new(@"IIS AppPool\" + pool);

    public FolderAccess? GetAccess(string folder, string pool)
    {
        var sid = (SecurityIdentifier)Account(pool).Translate(typeof(SecurityIdentifier));
        var rules = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow).ToList();
        FileSystemRights have = 0; foreach (var r in rules) have |= r.FileSystemRights;
        if ((have & Rights(FolderAccess.Modify)) == Rights(FolderAccess.Modify)) return FolderAccess.Modify;
        if ((have & Rights(FolderAccess.ReadExecute)) == Rights(FolderAccess.ReadExecute)) return FolderAccess.ReadExecute;
        if ((have & Rights(FolderAccess.Read)) == Rights(FolderAccess.Read)) return FolderAccess.Read;
        return null;
    }

    public void GrantAccess(string folder, string pool, FolderAccess access)
    {
        var di = new DirectoryInfo(folder);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(Account(pool), Rights(access), InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }
}

/// <summary>Development helper: an in-memory IIS, so the whole flow works on Linux.</summary>
public sealed class SampleIisAdmin(SamplePoolAdmin pools) : IIisAdmin
{
    private readonly Lock _gate = new();
    private readonly List<(long Id, string Name, bool Running, List<HttpBinding> Http, string Path, string Pool, List<(string Path, string Phys, string Pool)> Apps, List<(string App, string V, string Phys)> VDirs)> _sites = [];
    private readonly Dictionary<string, FolderAccess> _acl = new(StringComparer.OrdinalIgnoreCase);
    private long _next = 100;

    public IReadOnlyList<SiteInfo> Sites()
    {
        lock (_gate)
            return _sites.Select(s => new SiteInfo(s.Id, s.Name, s.Running ? "Started" : "Stopped", s.Http.ToList(),
                [new AppInfo("/", s.Pool, s.Path, [("/", s.Path)]), .. s.Apps.Select(a => new AppInfo(a.Path, a.Pool, a.Phys, [("/", a.Phys)]))])).ToList();
    }

    public bool PoolExists(string name) => pools.Get(name) is not null;
    public void CreatePool(string name, PoolConfig? config) { pools.Create(name, config); }

    public long CreateSite(string name, string physicalPath, string pool, IReadOnlyList<HttpBinding> bindings)
    {
        lock (_gate) { var id = ++_next; _sites.Add((id, name, false, bindings.ToList(), physicalPath, pool, [], [])); return id; }
    }

    public string SetSiteRunning(long siteId, bool running)
    {
        lock (_gate)
        {
            var i = _sites.FindIndex(s => s.Id == siteId); if (i < 0) throw new InvalidOperationException("Site not found.");
            _sites[i] = _sites[i] with { Running = running }; return running ? "Started" : "Stopped";
        }
    }

    public void SetHttpBindings(long siteId, IReadOnlyList<HttpBinding> bindings)
    {
        lock (_gate) { var i = _sites.FindIndex(s => s.Id == siteId); _sites[i] = _sites[i] with { Http = bindings.ToList() }; }
    }

    public void CreateApplication(long siteId, string appPath, string physicalPath, string pool)
    {
        lock (_gate) { var i = _sites.FindIndex(s => s.Id == siteId); _sites[i].Apps.Add((appPath, physicalPath, pool)); }
    }

    public void CreateVirtualDirectory(long siteId, string appPath, string virtualPath, string physicalPath)
    {
        lock (_gate) { var i = _sites.FindIndex(s => s.Id == siteId); _sites[i].VDirs.Add((appPath, virtualPath, physicalPath)); }
    }

    public FolderAccess? GetAccess(string folder, string pool) { lock (_gate) return _acl.TryGetValue(folder + "|" + pool, out var a) ? a : null; }
    public void GrantAccess(string folder, string pool, FolderAccess access) { lock (_gate) { var k = folder + "|" + pool; _acl[k] = _acl.TryGetValue(k, out var o) && o > access ? o : access; } }
}
