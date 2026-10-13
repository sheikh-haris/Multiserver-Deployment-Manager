using System.Runtime.Versioning;
using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Pools;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.Administration;
using Xunit;

namespace IisControlCenter.Agent.IntegrationTests;

/// <summary>Create pools, sites, applications, virtual directories, bindings and folder access on a real IIS, and clean up afterwards.</summary>
[SupportedOSPlatform("windows")]
public sealed class IisAdminIntegrationTests : IClassFixture<IisFixture>, IDisposable
{
    private readonly IisFixture fx;
    private readonly IisAdmin admin = new();
    private readonly string id = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> sites = [], pools = [];
    // Not under the user profile: the agent refuses roots inside \Users, \Windows and similar system folders (that is the rule being relied on).
    private string Root => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "icc-itadm-" + id);

    public IisAdminIntegrationTests(IisFixture fx) { this.fx = fx; }

    public void Dispose()
    {
        try
        {
            using var m = new ServerManager();
            foreach (var s in sites) m.Sites[s]?.Delete();
            foreach (var p in pools) m.ApplicationPools[p]?.Delete();
            m.CommitChanges();
        }
        catch { }
        try { Directory.Delete(Root, true); } catch { }
    }

    private void Need() => Skip.IfNot(fx.Available, fx.SkipReason);
    private string NewPool() { var n = "icc-it-adm-pool-" + id; if (!pools.Contains(n)) { admin.CreatePool(n, null); pools.Add(n); } return n; }

    [SkippableFact]
    public void A_pool_is_created_with_settings_and_a_second_create_is_not_this_layers_job()
    {
        Need();
        var n = "icc-it-adm-cfg-" + id; pools.Add(n);
        admin.CreatePool(n, new PoolConfig { QueueLength = 1234, IdleTimeoutMinutes = 5 });
        var c = new IisPoolAdmin().Get(n)!.Config;
        Assert.Equal((1234, 5), (c.QueueLength, c.IdleTimeoutMinutes));
        Assert.True(admin.PoolExists(n));
    }

    [SkippableFact]
    public void A_site_is_created_stopped_with_http_bindings_pool_and_folder_and_can_be_started_and_stopped()
    {
        Need();
        var pool = NewPool(); var name = "icc-it-adm-site-" + id; sites.Add(name);
        var dir = Path.Combine(Root, "site"); Directory.CreateDirectory(dir);
        var siteId = admin.CreateSite(name, dir, pool, [new HttpBinding($"a-{id}.icc-it.local", 18080), new HttpBinding($"b-{id}.icc-it.local", 18081)]);
        var s = admin.Sites().Single(x => x.Id == siteId);
        Assert.Equal("Stopped", s.State);
        Assert.Equal(2, s.HttpBindings.Count);
        Assert.Equal(pool, s.Apps.Single(a => a.Path == "/").PoolName);
        Assert.Equal(dir, s.Apps.Single(a => a.Path == "/").PhysicalPath, ignoreCase: true);

        Assert.Equal("Started", admin.SetSiteRunning(siteId, true));
        Assert.Equal("Started", admin.SetSiteRunning(siteId, true));
        Assert.Equal("Stopped", admin.SetSiteRunning(siteId, false));
    }

    [SkippableFact]
    public void Replacing_http_bindings_keeps_the_https_binding_and_its_certificate()
    {
        Need();
        var before = new ServerManager().Sites[fx.SiteName].Bindings.Single(b => b.Protocol == "https");
        var hash = Convert.ToHexString(before.CertificateHash);
        var siteId = new ServerManager().Sites[fx.SiteName].Id;
        var http = admin.Sites().Single(s => s.Id == siteId).HttpBindings;
        try
        {
            admin.SetHttpBindings(siteId, [new HttpBinding($"changed-{id}.icc-it.local", 18090)]);
            using var m = new ServerManager();
            var site = m.Sites[fx.SiteName];
            Assert.Equal(["changed-" + id + ".icc-it.local"], site.Bindings.Where(b => b.Protocol == "http").Select(b => b.Host));
            var https = site.Bindings.Single(b => b.Protocol == "https");
            Assert.Equal(hash, Convert.ToHexString(https.CertificateHash));
        }
        finally { admin.SetHttpBindings(siteId, http); }
    }

    [SkippableFact]
    public void Applications_and_virtual_directories_are_created_in_the_right_place()
    {
        Need();
        var pool = NewPool(); var name = "icc-it-adm-app-" + id; sites.Add(name);
        var dir = Path.Combine(Root, "web"); var api = Path.Combine(Root, "web", "api"); var files = Path.Combine(Root, "files"); var apiFiles = Path.Combine(Root, "apifiles");
        foreach (var d in new[] { dir, api, files, apiFiles }) Directory.CreateDirectory(d);
        var siteId = admin.CreateSite(name, dir, pool, [new HttpBinding($"c-{id}.icc-it.local", 18082)]);
        admin.CreateApplication(siteId, "/api", api, pool);
        admin.CreateVirtualDirectory(siteId, "/", "/files", files);
        admin.CreateVirtualDirectory(siteId, "/api", "/api/data", apiFiles);
        var s = admin.Sites().Single(x => x.Id == siteId);
        Assert.Contains(s.Apps, a => a.Path == "/api" && a.PoolName == pool);
        var root = s.Apps.Single(a => a.Path == "/");
        Assert.Contains(root.VirtualDirs, v => v.Path == "/files" && string.Equals(v.PhysicalPath, files, StringComparison.OrdinalIgnoreCase));
        var apiApp = s.Apps.Single(a => a.Path == "/api");
        Assert.Contains(apiApp.VirtualDirs, v => v.Path == "/data" && string.Equals(v.PhysicalPath, apiFiles, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void Folder_access_goes_to_the_pool_account_accumulates_and_is_read_back()
    {
        Need();
        var pool = NewPool();
        var dir = Path.Combine(Root, "data"); Directory.CreateDirectory(dir);
        Assert.Null(admin.GetAccess(dir, pool));
        admin.GrantAccess(dir, pool, FolderAccess.Read);
        Assert.Equal(FolderAccess.Read, admin.GetAccess(dir, pool));
        admin.GrantAccess(dir, pool, FolderAccess.ReadExecute);
        Assert.Equal(FolderAccess.ReadExecute, admin.GetAccess(dir, pool));
        admin.GrantAccess(dir, pool, FolderAccess.Modify);
        Assert.Equal(FolderAccess.Modify, admin.GetAccess(dir, pool));
        // a file created afterwards inherits it
        var f = Path.Combine(dir, "x.txt"); File.WriteAllText(f, "x");
        Assert.Contains(new FileInfo(f).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount)).Cast<System.Security.AccessControl.FileSystemAccessRule>(),
            r => r.IdentityReference.Value.EndsWith(pool, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task The_handler_enforces_its_rules_against_real_IIS()
    {
        Need();
        var me = Guid.NewGuid(); var clock = TimeProvider.System;
        Directory.CreateDirectory(Root);
        var cfg = new AgentConfig { PortalUrl = "https://agents.local", Environment = "Dev", AllowedRoots = [Root], ServerId = me, AllowManaged = true };
        var h = new IisOpHandler(new CommandPolicy(cfg, clock), cfg, admin, new GuardedPaths(cfg), NullLogger<IisOpHandler>.Instance);
        CommandEnvelope Env() => new(Guid.NewGuid(), me, "Dev", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "c", ServerMode.Managed);
        var pool = NewPool(); var name = "icc-it-adm-h-" + id; sites.Add(name);

        var ok = await h.RunAsync(Env(), new IisOpCommand { Op = IisOp.CreateSite, SiteName = name, PhysicalPath = Path.Combine(Root, "h-site"), CreateFolder = true, PoolName = pool, Bindings = [new HttpBinding($"h-{id}.icc-it.local", 18083)] });
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal("Stopped", admin.Sites().Single(s => s.Id == ok.SiteId).State);
        Assert.True(Directory.Exists(Path.Combine(Root, "h-site")));

        var outside = await h.RunAsync(Env(), new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = Path.Combine(Path.GetTempPath(), "icc-outside-" + id) });
        Assert.False(outside.Ok);
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "icc-outside-" + id)));
        var dup = await h.RunAsync(Env(), new IisOpCommand { Op = IisOp.CreateSite, SiteName = name + "2", PhysicalPath = Path.Combine(Root, "h2"), CreateFolder = true, PoolName = pool, Bindings = [new HttpBinding($"h-{id}.icc-it.local", 18083)] });
        Assert.False(dup.Ok); Assert.Contains("already bound", dup.Error);
        var sameFolder = await h.RunAsync(Env(), new IisOpCommand { Op = IisOp.CreateSite, SiteName = name + "3", PhysicalPath = Path.Combine(Root, "h-site"), PoolName = pool, Bindings = [new HttpBinding($"z-{id}.icc-it.local", 18084)] });
        Assert.False(sameFolder.Ok); Assert.Contains("already used", sameFolder.Error);
    }
}
