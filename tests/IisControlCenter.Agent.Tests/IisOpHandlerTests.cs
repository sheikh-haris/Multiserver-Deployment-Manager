using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Pools;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IisControlCenter.Agent.Tests;

/// <summary>Path rules for a Linux test machine (the real ones are Windows rules, covered by PathGuardTests and the Windows CI job).</summary>
internal sealed class PosixTestPaths(string root) : IPathChecks
{
    public string? Normalize(string? p) => string.IsNullOrWhiteSpace(p) || !p.StartsWith('/') || p.Split('/').Any(x => x == "..") ? null : p.TrimEnd('/');
    public bool IsUnderRoot(string? p) => Normalize(p) is { } n && (n == root || n.StartsWith(root + "/", StringComparison.Ordinal));
    public bool IsStrictlyUnderRoot(string? p) => Normalize(p) is { } n && n != root && IsUnderRoot(p);
}

public class IisOpHandlerTests : IDisposable
{
    private static readonly Guid Me = Guid.NewGuid();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly string root = Path.Combine(Path.GetTempPath(), "icc-iisop-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SamplePoolAdmin pools = new();
    private readonly SampleIisAdmin iis;
    private readonly IisOpHandler h;

    public IisOpHandlerTests()
    {
        Directory.CreateDirectory(root);
        iis = new SampleIisAdmin(pools);
        var cfg = new AgentConfig { PortalUrl = "https://agents.local", Environment = "Dev", AllowedRoots = [root], ServerId = Me, AllowManaged = true };
        h = new IisOpHandler(new CommandPolicy(cfg, clock), cfg, iis, new PosixTestPaths(root), NullLogger<IisOpHandler>.Instance);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    private CommandEnvelope Env(ServerMode m = ServerMode.Managed, string env = "Dev") => new(Guid.NewGuid(), Me, env, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "c", m);
    private string P(string rel) => Path.Combine(root, rel);
    private static HttpBinding B(string host, int port = 80) => new(host, port);
    private Task<IisOpResult> Run(IisOpCommand c, ServerMode m = ServerMode.Managed) => h.RunAsync(Env(m), c);

    private async Task<long> NewSite(string name = "shop", string host = "shop.test", int port = 80)
    {
        var r = await Run(new IisOpCommand { Op = IisOp.CreateSite, SiteName = name, PhysicalPath = P(name), CreateFolder = true, PoolName = "DefaultAppPool", Bindings = [B(host, port)] });
        Assert.True(r.Ok, r.Error); return r.SiteId!.Value;
    }

    // ---------------- gates ----------------

    [Fact]
    public async Task Nothing_runs_on_a_read_only_server_or_with_a_bad_envelope()
    {
        var c = new IisOpCommand { Op = IisOp.CreatePool, PoolName = "NewPool" };
        Assert.Contains("ReadOnly", (await Run(c, ServerMode.ReadOnly)).Error);
        Assert.Contains("Prod", (await h.RunAsync(Env(env: "Prod"), c)).Error);
        Assert.DoesNotContain("NewPool", pools.Names);
    }

    // ---------------- pools ----------------

    [Fact]
    public async Task A_pool_can_be_created_once_with_validated_settings()
    {
        Assert.True((await Run(new IisOpCommand { Op = IisOp.CreatePool, PoolName = "AppA", PoolConfig = new PoolConfig { QueueLength = 2000 } })).Ok);
        Assert.Equal(2000, pools.Get("AppA")!.Config.QueueLength);
        Assert.Contains("already exists", (await Run(new IisOpCommand { Op = IisOp.CreatePool, PoolName = "AppA" })).Error);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.CreatePool, PoolName = "AppB", PoolConfig = new PoolConfig { IdentityType = "LocalSystem" } })).Ok);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.CreatePool, PoolName = "..\\x" })).Ok);
    }

    // ---------------- sites ----------------

    [Fact]
    public async Task A_site_is_created_stopped_with_its_folder_pool_and_http_bindings()
    {
        var id = await NewSite();
        var s = iis.Sites().Single();
        Assert.Equal(("shop", "Stopped"), (s.Name, s.State));
        Assert.Equal([B("shop.test")], s.HttpBindings);
        Assert.True(Directory.Exists(P("shop")));
        Assert.Equal("Started", (await Run(new IisOpCommand { Op = IisOp.StartSite, SiteId = id })).After);
        var again = await Run(new IisOpCommand { Op = IisOp.StartSite, SiteId = id });
        Assert.True(again.Ok); Assert.False(again.Changed);
        Assert.Equal("Stopped", (await Run(new IisOpCommand { Op = IisOp.StopSite, SiteId = id })).After);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.StartSite, SiteId = 9999 })).Ok);
    }

    [Theory]
    [InlineData("", 80)] [InlineData("*", 80)] [InlineData("a.test", 79)] [InlineData("a.test", 443)] [InlineData("a.test", 70000)] [InlineData("not a host", 80)] [InlineData("a_b.test", 80)]
    public async Task Bad_bindings_are_refused(string host, int port)
    {
        var r = await Run(new IisOpCommand { Op = IisOp.CreateSite, SiteName = "x", PhysicalPath = P("x"), CreateFolder = true, PoolName = "DefaultAppPool", Bindings = [B(host, port)] });
        Assert.False(r.Ok);
        Assert.Empty(iis.Sites());
        Assert.False(Directory.Exists(P("x")));         // nothing was created on the way
    }

    [Fact]
    public async Task A_site_cannot_reuse_a_name_a_binding_a_folder_or_an_unknown_pool()
    {
        await NewSite();
        IisOpCommand Make(string name = "other", string host = "other.test", string folder = "other", string pool = "DefaultAppPool") =>
            new() { Op = IisOp.CreateSite, SiteName = name, PhysicalPath = P(folder), CreateFolder = true, PoolName = pool, Bindings = [B(host)] };
        Assert.Contains("already exists", (await Run(Make(name: "SHOP"))).Error);
        Assert.Contains("already bound", (await Run(Make(host: "Shop.Test"))).Error);
        Assert.Contains("already used", (await Run(Make(folder: "shop"))).Error);
        Assert.Contains("does not exist", (await Run(Make(pool: "NoPool"))).Error);
        Assert.Single(iis.Sites());
    }

    [Fact]
    public async Task Folders_must_be_strictly_inside_an_allowed_root_without_dotdot_or_links()
    {
        IisOpCommand At(string path) => new() { Op = IisOp.CreateSite, SiteName = "x", PhysicalPath = path, CreateFolder = true, PoolName = "DefaultAppPool", Bindings = [B("x.test")] };
        Assert.False((await Run(At(root))).Ok);                                                       // the root itself
        Assert.False((await Run(At(Path.GetTempPath() + "elsewhere"))).Ok);                           // outside
        Assert.False((await Run(At(root + "/../escape"))).Ok);                                        // ..
        Assert.False((await Run(At("relative/path"))).Ok);
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "escape")));

        var target = Path.Combine(Path.GetTempPath(), "icc-link-target-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(P("link"), target);                                          // a symlink inside the root pointing outside
            var r = await Run(At(P("link/site")));
            Assert.False(r.Ok); Assert.Contains("link", r.Error);
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally { Directory.Delete(target, true); }
    }

    [Fact]
    public async Task A_missing_folder_is_only_created_when_asked_to()
    {
        var c = new IisOpCommand { Op = IisOp.CreateSite, SiteName = "x", PhysicalPath = P("newdir"), PoolName = "DefaultAppPool", Bindings = [B("x.test")] };
        Assert.Contains("does not exist", (await Run(c)).Error);
        Assert.True((await Run(c with { CreateFolder = true })).Ok);
    }

    // ---------------- bindings ----------------

    [Fact]
    public async Task Bindings_are_replaced_only_when_the_person_saw_the_current_ones_and_nothing_collides()
    {
        var id = await NewSite();
        await NewSite("blog", "blog.test");
        IisOpCommand Upd(IReadOnlyList<HttpBinding> want, IReadOnlyList<HttpBinding> saw) => new() { Op = IisOp.UpdateBindings, SiteId = id, Bindings = want, ExpectedBindings = saw };

        var ok = await Run(Upd([B("shop.test"), B("www.shop.test", 8080)], [B("shop.test")]));
        Assert.True(ok.Ok, ok.Error); Assert.True(ok.Changed);
        Assert.Equal(2, iis.Sites().First(s => s.Id == id).HttpBindings.Count);

        var stale = await Run(Upd([B("shop.test")], [B("shop.test")]));                                // the live list has two now
        Assert.False(stale.Ok); Assert.Contains("someone else", stale.Error);
        var clash = await Run(Upd([B("blog.test")], [B("shop.test"), B("www.shop.test", 8080)]));
        Assert.False(clash.Ok); Assert.Contains("already bound", clash.Error);
        Assert.False((await Run(Upd([], [B("shop.test"), B("www.shop.test", 8080)]))).Ok);            // at least one
        var same = await Run(Upd([B("www.shop.test", 8080), B("shop.test")], [B("shop.test"), B("www.shop.test", 8080)]));
        Assert.True(same.Ok); Assert.False(same.Changed);
    }

    // ---------------- applications and virtual directories ----------------

    [Fact]
    public async Task Applications_and_virtual_directories_follow_the_same_rules()
    {
        var id = await NewSite();
        var app = await Run(new IisOpCommand { Op = IisOp.CreateApplication, SiteId = id, AppPath = "/api", PhysicalPath = P("shop/api"), CreateFolder = true, PoolName = "DefaultAppPool" });
        Assert.True(app.Ok, app.Error);
        Assert.Contains("already exists", (await Run(new IisOpCommand { Op = IisOp.CreateApplication, SiteId = id, AppPath = "/API", PhysicalPath = P("shop/api2"), CreateFolder = true, PoolName = "DefaultAppPool" })).Error);
        Assert.Contains("already used", (await Run(new IisOpCommand { Op = IisOp.CreateApplication, SiteId = id, AppPath = "/other", PhysicalPath = P("shop/api"), PoolName = "DefaultAppPool" })).Error);
        foreach (var bad in new[] { "api", "/", "/a b/", "/..", "/a//b", "/x;y" })
            Assert.False((await Run(new IisOpCommand { Op = IisOp.CreateApplication, SiteId = id, AppPath = bad, PhysicalPath = P("shop/z"), CreateFolder = true, PoolName = "DefaultAppPool" })).Ok, bad);

        var v = await Run(new IisOpCommand { Op = IisOp.CreateVirtualDirectory, SiteId = id, AppPath = "/", VirtualPath = "/files", PhysicalPath = P("files"), CreateFolder = true });
        Assert.True(v.Ok, v.Error);
        Assert.Contains("already exists", (await Run(new IisOpCommand { Op = IisOp.CreateVirtualDirectory, SiteId = id, AppPath = "/", VirtualPath = "/api", PhysicalPath = P("files2"), CreateFolder = true })).Error);
        Assert.Contains("must be inside", (await Run(new IisOpCommand { Op = IisOp.CreateVirtualDirectory, SiteId = id, AppPath = "/api", VirtualPath = "/files3", PhysicalPath = P("files3"), CreateFolder = true })).Error);
        Assert.Contains("does not exist", (await Run(new IisOpCommand { Op = IisOp.CreateVirtualDirectory, SiteId = id, AppPath = "/nope", VirtualPath = "/nope/x", PhysicalPath = P("files4"), CreateFolder = true })).Error);
    }

    // ---------------- folders and access ----------------

    [Fact]
    public async Task Folders_are_created_inside_the_roots_only_and_are_idempotent()
    {
        var r = await Run(new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = P("data/uploads") });
        Assert.True(r.Ok && r.Changed); Assert.True(Directory.Exists(P("data/uploads")));
        var again = await Run(new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = P("data/uploads") });
        Assert.True(again.Ok); Assert.False(again.Changed);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = root })).Ok);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = "/etc/evil" })).Ok);
    }

    [Fact]
    public async Task Access_goes_only_to_an_existing_pools_account_on_an_existing_folder_inside_the_roots_and_never_downgrades()
    {
        Directory.CreateDirectory(P("data"));
        IisOpCommand G(FolderAccess a, string pool = "DefaultAppPool", string? path = null) => new() { Op = IisOp.GrantFolderAccess, PoolName = pool, PhysicalPath = path ?? P("data"), Access = a };
        var read = await Run(G(FolderAccess.ReadExecute)); Assert.True(read.Ok && read.Changed);
        var modify = await Run(G(FolderAccess.Modify)); Assert.True(modify.Ok && modify.Changed);
        Assert.Equal(FolderAccess.Modify, iis.GetAccess(P("data"), "DefaultAppPool"));
        var down = await Run(G(FolderAccess.Read)); Assert.True(down.Ok); Assert.False(down.Changed);          // already has more
        Assert.Equal(FolderAccess.Modify, iis.GetAccess(P("data"), "DefaultAppPool"));

        Assert.Contains("does not exist", (await Run(G(FolderAccess.Read, pool: "NoPool"))).Error);
        Assert.Contains("does not exist", (await Run(G(FolderAccess.Read, path: P("missing")))).Error);
        Assert.False((await Run(G(FolderAccess.Read, path: root))).Ok);                               // the root itself
        Assert.False((await Run(G(FolderAccess.Read, path: "/etc"))).Ok);
        Assert.False((await Run(new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "DefaultAppPool", PhysicalPath = P("data") })).Ok);       // no access level
        Assert.False((await Run(G(FolderAccess.Read, pool: "IIS AppPool\\x\\..\\Administrators"))).Ok);                                                // not a pool name
    }
}
