using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public class IisOpsTests : IDisposable
{
    private readonly TestEnv t = new();
    private readonly PendingAgentResults pending;
    private readonly IisOpsService svc;
    private readonly PoolAdminService pools;
    private Guid serverId, projectId, otherProjectId, compId;
    private long nextSite = 500;
    public void Dispose() => t.Dispose();

    public IisOpsTests()
    {
        pending = new PendingAgentResults(t.Clock);
        svc = new IisOpsService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock);
        pools = new PoolAdminService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock, svc);
        t.Hub.Client.OnIisOp = (env, c) =>
        {
            var changed = c.Op != IisOp.CreateFolder || true;
            pending.CompleteIis(env.ServerId, new IisOpResult(env.CommandId, true, null, "before", "after: " + IisOpsService.Describe(c), changed, c.Op == IisOp.CreateSite ? ++nextSite : c.SiteId));
            return Task.CompletedTask;
        };
    }

    /// <summary>A Managed, online server in the given environment. Project "Payments" has a target (site 1, pool PayPool, folder D:\Websites\pay) on it; project "Other" has site 2.</summary>
    private async Task Setup(string env = "Dev", bool managed = true)
    {
        serverId = await t.ApprovedServer("IIS01", env);
        await t.Store(serverId, new SiteSpec(1, "pay", @"D:\Websites\pay", "PayPool"), new SiteSpec(2, "secret", @"D:\Websites\finance\app", "FinPool"), new SiteSpec(3, "free", @"D:\Websites\free", "FreePool"));
        projectId = await t.NewProject("Payments"); otherProjectId = await t.NewProject("Other");
        compId = await t.NewComponent(projectId, "Web");
        var otherComp = await t.NewComponent(otherProjectId, "Fin");
        await t.Link(compId, serverId, 1); await t.Link(otherComp, serverId, 2);
        await using var db = t.Db.CreateDbContext();
        var s = await db.Servers.FirstAsync(x => x.Id == serverId);
        s.Mode = managed ? ServerMode.Managed : ServerMode.ReadOnly; s.LocalAllowManaged = true;
        await db.SaveChangesAsync();
        t.Tracker.Add(serverId, "conn");
    }

    private Actor Operator(string env = "Dev", string id = "op") => t.ActorWith(Role.ServerOperator, env, id);

    private Actor Owner(Guid? project = null, string id = "own")
    {
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Project, ProjectId = project ?? projectId, Role = Role.ProjectOwner };
        return new Actor(id, "Owner " + id, null, new AccessEvaluator(new UserContext(id, "Owner", []), [a], t.Clock.GetUtcNow()));
    }

    private static IisOpCommand Site(string name = "shop", string folder = @"D:\Websites\shop", string pool = "FreePool") =>
        new() { Op = IisOp.CreateSite, SiteName = name, PhysicalPath = folder, CreateFolder = true, PoolName = pool, Bindings = [new HttpBinding("shop.test", 80)] };

    private async Task<List<(string Action, string Result, string? After, string? Detail)>> Audit(string prefix = "Iis.")
    {
        await using var db = t.Db.CreateDbContext();
        return (await db.AuditLog.Where(a => a.Action.StartsWith(prefix)).OrderBy(a => a.Id).ToListAsync()).Select(a => (a.Action, a.Result, a.After, a.Detail)).ToList();
    }

    // ---------------- operators ----------------

    [Fact]
    public async Task An_operator_creates_a_site_in_dev_it_is_audited_recorded_and_triggers_a_discovery()
    {
        await Setup();
        var r = await svc.RunAsync(Operator(), serverId, Site(), null, null);
        Assert.True(r.Ok, r.Error); Assert.False(r.Queued);
        Assert.Equal(IisOp.CreateSite, t.Hub.Client.IisOpsSent.Single().Command.Op);
        Assert.Contains(await Audit(), a => a.Action == "Iis.CreateSite" && a.Result == "Success" && a.After!.Contains("shop"));
        await using var db = t.Db.CreateDbContext();
        var item = await db.IisCreatedItems.SingleAsync();
        Assert.Equal((IisItemKind.Site, @"D:\Websites\shop", (Guid?)null), (item.Kind, item.Path, item.ProjectId));
        Assert.Single(t.Hub.Client.Discoveries);
    }

    [Fact]
    public async Task Bad_requests_are_refused_before_the_agent_is_contacted()
    {
        await Setup();
        var op = Operator();
        foreach (var bad in new[]
        {
            Site() with { Bindings = [new HttpBinding("*", 80)] }, Site() with { Bindings = [new HttpBinding("a.test", 443)] }, Site() with { Bindings = [] },
            Site(name: "bad/name"), Site() with { PoolName = null },
            new IisOpCommand { Op = IisOp.CreateApplication, SiteId = 1, AppPath = "api", PhysicalPath = @"D:\x", PoolName = "P" },
            new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "..\\x", PhysicalPath = @"D:\x", Access = FolderAccess.Read },
            new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "P", PhysicalPath = @"D:\x" },
            new IisOpCommand { Op = IisOp.UpdateBindings, SiteId = 1, Bindings = [new HttpBinding("a.test", 80), new HttpBinding("A.test", 80)] },
        })
            Assert.False((await svc.RunAsync(op, serverId, bad, null, null)).Ok);
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    [Fact]
    public async Task Everyone_without_the_right_role_is_refused_and_it_is_audited()
    {
        await Setup();
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RunAsync(t.ActorWith(Role.Auditor), serverId, Site(), null, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RunAsync(Operator("Prod", "other-env"), serverId, Site(), null, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RunAsync(t.Nobody, serverId, Site(), null, null));
        Assert.Equal(3, (await Audit()).Count(a => a.Result == "Denied"));
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    [Fact]
    public async Task A_read_only_server_and_an_offline_agent_stop_everything()
    {
        await Setup(managed: false);
        Assert.Contains("ReadOnly", (await svc.RunAsync(Operator(), serverId, Site(), null, null)).Error);
        await using (var db = t.Db.CreateDbContext()) { (await db.Servers.FirstAsync()).Mode = ServerMode.Managed; await db.SaveChangesAsync(); }
        t.Tracker.Remove(serverId, "conn");
        Assert.Contains("offline", (await svc.RunAsync(Operator(), serverId, Site(), null, null)).Error);
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    [Fact]
    public async Task Modify_access_needs_a_reason_and_has_its_own_audit_action()
    {
        await Setup();
        var grant = new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "PayPool", PhysicalPath = @"D:\Websites\pay\uploads", Access = FolderAccess.Modify };
        Assert.Contains("reason", (await svc.RunAsync(Operator(), serverId, grant, null, " ")).Error);
        Assert.True((await svc.RunAsync(Operator(), serverId, grant, null, "the app writes uploads")).Ok);
        Assert.True((await svc.RunAsync(Operator(), serverId, grant with { Access = FolderAccess.ReadExecute }, null, null)).Ok);
        var a = await Audit();
        Assert.Contains(a, x => x.Action == "Iis.GrantFolderAccess.Modify" && x.Detail == "the app writes uploads");
        Assert.Contains(a, x => x.Action == "Iis.GrantFolderAccess");
    }

    // ---------------- Production ----------------

    [Fact]
    public async Task In_production_every_change_except_start_waits_for_a_second_operator()
    {
        await Setup("Prod");
        var op = Operator("Prod");
        Assert.Contains("reason", (await svc.RunAsync(op, serverId, Site(), null, null)).Error);
        var q = await svc.RunAsync(op, serverId, Site(), null, "new reporting site");
        Assert.True(q.Ok && q.Queued);
        Assert.Empty(t.Hub.Client.IisOpsSent);

        var own = await pools.DecideAsync(op, q.RequestId!.Value, true, null);
        Assert.False(own.Ok);
        var op2 = Operator("Prod", "op2");
        Assert.True((await pools.DecideAsync(op2, q.RequestId.Value, true, "ok")).Ok);
        Assert.Equal(IisOp.CreateSite, t.Hub.Client.IisOpsSent.Single().Command.Op);
        Assert.Contains(await Audit(), a => a.Action == "Iis.CreateSite" && a.Detail!.Contains("Approved by User op2") && a.Detail.Contains("new reporting site"));
        var row = (await pools.RequestsAsync(op)).Single();
        Assert.Equal((PoolRequestKind.Iis, PoolRequestStatus.Executed), (row.Request.Kind, row.Request.Status));
        Assert.Contains("Create site shop", row.ChangeSummary);

        // start is direct
        Assert.True((await svc.RunAsync(op, serverId, new IisOpCommand { Op = IisOp.StartSite, SiteId = 3 }, null, null)).Ok);
        // stop is not
        Assert.True((await svc.RunAsync(op, serverId, new IisOpCommand { Op = IisOp.StopSite, SiteId = 3 }, null, "maintenance")).Queued);
    }

    [Fact]
    public async Task A_rejected_production_request_changes_nothing()
    {
        await Setup("Prod");
        var q = await svc.RunAsync(Operator("Prod"), serverId, Site(), null, "x");
        Assert.True((await pools.DecideAsync(Operator("Prod", "op2"), q.RequestId!.Value, false, "no")).Ok);
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    // ---------------- project Owners ----------------

    [Fact]
    public async Task An_owner_can_create_their_own_pool_site_and_application_in_dev_with_new_folders_only()
    {
        await Setup();
        var o = Owner();
        Assert.True((await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.CreatePool, PoolName = "PayApi" }, projectId, null)).Ok);
        var site = await svc.RunAsync(o, serverId, Site("payapi", @"D:\Websites\payapi", "PayApi"), projectId, null);
        Assert.True(site.Ok, site.Error);
        var sent = t.Hub.Client.IisOpsSent.Last().Command;
        Assert.True(sent.RequireNewFolder && sent.CreateFolder);                                // an Owner can never point a new site at an existing folder
        var siteId = site.Result!.SiteId!.Value;
        var app = await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.CreateApplication, SiteId = siteId, AppPath = "/v2", PhysicalPath = @"D:\Websites\payapi\v2", PoolName = "PayApi" }, projectId, null);
        Assert.True(app.Ok, app.Error);
        Assert.True((await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.StartSite, SiteId = siteId }, projectId, null)).Ok);
        Assert.True((await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "PayApi", PhysicalPath = @"D:\Websites\payapi", Access = FolderAccess.ReadExecute }, projectId, null)).Ok);
        await using var db = t.Db.CreateDbContext();
        Assert.All(db.IisCreatedItems.ToList(), i => Assert.Equal(projectId, i.ProjectId));
    }

    [Fact]
    public async Task An_owner_is_limited_to_their_own_project_dev_and_staging_and_their_own_items()
    {
        await Setup();
        var o = Owner();
        async Task<string?> Err(IisOpCommand c, Actor? who = null, Guid? p = null) { try { return (await svc.RunAsync(who ?? o, serverId, c, p ?? projectId, null)).Error; } catch (ForbiddenException ex) { return ex.Message; } }

        Assert.Contains("pool of your project", await Err(Site("x", @"D:\Websites\x", "FreePool")));                                  // not their pool
        Assert.Contains("pool of your project", await Err(Site("x", @"D:\Websites\x", "FinPool")));                                   // another project's pool
        await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.CreatePool, PoolName = "Mine" }, projectId, null);
        Assert.Contains("another project", await Err(Site("x", @"D:\Websites\finance\app\sub", "Mine")));                              // inside another project's folder
        Assert.Contains("does not belong", await Err(new IisOpCommand { Op = IisOp.StopSite, SiteId = 2 }));                          // another project's site
        Assert.Contains("does not belong", await Err(new IisOpCommand { Op = IisOp.UpdateBindings, SiteId = 3, Bindings = [new HttpBinding("a.test", 80)] }));   // an unowned site
        Assert.Contains("only a", (await Err(new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "Mine", PhysicalPath = @"D:\Websites\pay", Access = FolderAccess.Modify }))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("folders of your project", await Err(new IisOpCommand { Op = IisOp.GrantFolderAccess, PoolName = "Mine", PhysicalPath = @"D:\Websites\finance\app", Access = FolderAccess.Read }));
        Assert.Contains("inside folders of your project", await Err(new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = @"D:\Websites\elsewhere" }));
        Assert.Contains("You need", await Err(Site(), Owner(otherProjectId, "x2"), projectId));                                       // owner of ANOTHER project claiming this one
        Assert.Contains("You need", await Err(Site(), t.Nobody));
        Assert.True((await svc.RunAsync(o, serverId, new IisOpCommand { Op = IisOp.CreateFolder, PhysicalPath = @"D:\Websites\pay\data" }, projectId, null)).Ok);   // inside their own target folder
    }

    [Fact]
    public async Task An_owner_cannot_use_a_server_where_the_project_has_no_target_or_production()
    {
        await Setup();
        var other = await t.ApprovedServer("IIS02", "Dev");
        await using (var db = t.Db.CreateDbContext()) { (await db.Servers.FirstAsync(s => s.Id == other)).Mode = ServerMode.Managed; await db.SaveChangesAsync(); }
        t.Tracker.Add(other, "c2");
        var r = await svc.RunAsync(Owner(), other, new IisOpCommand { Op = IisOp.CreatePool, PoolName = "X" }, projectId, null);
        Assert.False(r.Ok); Assert.Contains("no deployment target on this server", r.Error);

        var prod = await t.ApprovedServer("PROD1", "Prod");
        await using (var db = t.Db.CreateDbContext()) { (await db.Servers.FirstAsync(s => s.Id == prod)).Mode = ServerMode.Managed; await db.SaveChangesAsync(); }
        t.Tracker.Add(prod, "c3");
        var p = await svc.RunAsync(Owner(), prod, new IisOpCommand { Op = IisOp.CreatePool, PoolName = "X" }, projectId, null);
        Assert.False(p.Ok); Assert.Contains("Production", p.Error);
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    // ---------------- page ----------------

    [Fact]
    public async Task The_page_for_an_owner_shows_only_their_pools_and_marks_their_sites_and_a_stranger_gets_nothing()
    {
        await Setup();
        var o = Owner();
        var page = (await svc.GetPageAsync(o, serverId, projectId))!;
        Assert.True(page.OwnerMode); Assert.True(page.CanAct);
        Assert.Equal(["PayPool"], page.Pools);
        Assert.Equal([1L], page.Sites.Where(s => s.Owned).Select(s => s.SiteId));
        Assert.Null(await svc.GetPageAsync(o, serverId, null));
        Assert.Null(await svc.GetPageAsync(Owner(otherProjectId, "x"), serverId, projectId));
        Assert.Null(await svc.GetPageAsync(t.Nobody, serverId, null));

        var op = (await svc.GetPageAsync(Operator(), serverId, null))!;
        Assert.False(op.OwnerMode); Assert.True(op.CanModify);
        Assert.Equal(3, op.Pools.Count);
    }
}
