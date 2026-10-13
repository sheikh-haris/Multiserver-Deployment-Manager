using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public class SiteTemplateServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    private readonly IisOpsService iis;
    private readonly PoolAdminService pools;
    private readonly SiteTemplateService svc;
    private Guid serverId, projectId;
    private long nextSite = 700;
    public void Dispose() => t.Dispose();

    public SiteTemplateServiceTests()
    {
        var pending = new PendingAgentResults(t.Clock);
        iis = new IisOpsService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock);
        pools = new PoolAdminService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock, iis);
        svc = new SiteTemplateService(t.Db, iis, t.Audit, t.Clock);
        t.Hub.Client.OnIisOp = (env, c) =>
        {
            if (c.PoolName == "Boom") { pending.CompleteIis(env.ServerId, new IisOpResult(env.CommandId, false, "agent said no", null, null, false, null)); return Task.CompletedTask; }
            pending.CompleteIis(env.ServerId, new IisOpResult(env.CommandId, true, null, "before", "after", true, c.Op == IisOp.CreateSite ? ++nextSite : c.SiteId));
            return Task.CompletedTask;
        };
    }

    private async Task Setup(string env = "Dev")
    {
        serverId = await t.ApprovedServer("IIS01", env);
        await t.Store(serverId, new SiteSpec(1, "pay", @"D:\Websites\pay", "PayPool"));
        projectId = await t.NewProject("Payments");
        var comp = await t.NewComponent(projectId, "Web");
        await t.Link(comp, serverId, 1);
        await using var db = t.Db.CreateDbContext();
        var s = await db.Servers.FirstAsync(x => x.Id == serverId);
        s.Mode = ServerMode.Managed; s.LocalAllowManaged = true;
        await db.SaveChangesAsync();
        t.Tracker.Add(serverId, "conn");
    }

    private Actor Operator(string env = "Dev", string id = "op") => t.ActorWith(Role.ServerOperator, env, id);
    private Actor Owner(string id = "own")
    {
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Project, ProjectId = projectId, Role = Role.ProjectOwner };
        return new Actor(id, "Owner " + id, null, new AccessEvaluator(new UserContext(id, "Owner", []), [a], t.Clock.GetUtcNow()));
    }

    private static SiteTemplateDef Def(bool modify = false) => new()
    {
        Folders = ["logs"], Apps = [new("/api", "api")],
        Access = modify ? [new("logs", FolderAccess.Modify)] : [new("", FolderAccess.ReadExecute)],
    };
    private static TemplateApply Input(string pool = "ShopPool") => new("shop", @"D:\Websites\shop", pool, [new HttpBinding("shop.test", 80)]);

    [Fact]
    public async Task Only_operators_and_admins_manage_templates_and_the_name_must_be_unique()
    {
        await Setup();
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateAsync(Owner(), "x", null, Def()));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.CreateAsync(t.Nobody, "x", null, Def()));
        var ok = await svc.CreateAsync(Operator(), "Standard", "desc", Def());
        Assert.True(ok.Ok, ok.Error);
        Assert.False((await svc.CreateAsync(Operator(), "Standard", null, Def())).Ok);
    }

    [Fact]
    public async Task Bad_templates_are_refused_and_good_ones_can_be_listed_renamed_and_removed()
    {
        await Setup();
        Assert.False((await svc.CreateAsync(Operator(), "Bad", null, new SiteTemplateDef { Folders = ["../x"] })).Ok);
        var id = (await svc.CreateAsync(Operator(), "Standard", null, Def())).Value;
        Assert.Single(await svc.ListAsync(Operator()));
        Assert.Single(await svc.ListAsync(Owner()));
        Assert.Empty(await svc.ListAsync(t.Nobody));
        Assert.True((await svc.UpdateAsync(Operator(), id, "Renamed", "d", Def(modify: true))).Ok);
        Assert.True((await svc.ListAsync(Operator())).Single().Def.HasModify);
        Assert.True((await svc.RemoveAsync(Operator(), id)).Ok);
        Assert.Empty(await svc.ListAsync(Operator()));
        await using var db = t.Db.CreateDbContext();
        var actions = await db.AuditLog.Where(a => a.Action.StartsWith("Template.")).Select(a => a.Action).ToListAsync();
        Assert.Contains("Template.Create", actions); Assert.Contains("Template.Update", actions); Assert.Contains("Template.Remove", actions);
    }

    [Fact]
    public async Task An_operator_applies_a_template_in_dev_and_the_new_site_id_reaches_later_steps()
    {
        await Setup();
        var id = (await svc.CreateAsync(Operator(), "Standard", null, Def())).Value;
        var r = await svc.ApplyAsync(Operator(), serverId, id, Input(), null, null);
        Assert.True(r.Ok, r.Error);
        var sent = t.Hub.Client.IisOpsSent.Select(s => s.Command).ToList();
        Assert.Equal([IisOp.CreatePool, IisOp.CreateSite, IisOp.CreateFolder, IisOp.CreateApplication, IisOp.GrantFolderAccess], sent.Select(s => s.Op).ToArray());
        Assert.Equal(701, sent.Single(s => s.Op == IisOp.CreateApplication).SiteId);
        Assert.Equal(sent.Count, r.Steps.Count);
        await using var db = t.Db.CreateDbContext();
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "Template.Apply");
    }

    [Fact]
    public async Task A_failing_step_stops_the_rest()
    {
        await Setup();
        var id = (await svc.CreateAsync(Operator(), "Standard", null, Def())).Value;
        var r = await svc.ApplyAsync(Operator(), serverId, id, Input("Boom"), null, null);
        Assert.False(r.Ok);
        Assert.Contains("not undone", r.Error);
        Assert.Single(t.Hub.Client.IisOpsSent);
    }

    [Fact]
    public async Task Bad_input_is_refused_before_the_agent_is_contacted()
    {
        await Setup();
        var id = (await svc.CreateAsync(Operator(), "Standard", null, Def())).Value;
        Assert.False((await svc.ApplyAsync(Operator(), serverId, id, Input() with { SiteName = "a/b" }, null, null)).Ok);
        Assert.False((await svc.ApplyAsync(Operator(), serverId, id, Input() with { Bindings = [new HttpBinding("*", 80)] }, null, null)).Ok);
        Assert.False((await svc.ApplyAsync(Operator(), serverId, id, Input() with { RootFolder = " " }, null, null)).Ok);
        Assert.False((await svc.ApplyAsync(Operator(), serverId, Guid.NewGuid(), Input(), null, null)).Ok);
        Assert.Empty(t.Hub.Client.IisOpsSent);
    }

    [Fact]
    public async Task In_production_the_whole_template_is_one_request_a_second_operator_approves()
    {
        await Setup("Prod");
        var id = (await svc.CreateAsync(Operator("Prod"), "Standard", null, Def())).Value;
        Assert.Contains("reason", (await svc.ApplyAsync(Operator("Prod"), serverId, id, Input(), null, null)).Error);
        var q = await svc.ApplyAsync(Operator("Prod"), serverId, id, Input(), null, "new shop");
        Assert.True(q.Ok && q.Queued);
        Assert.Empty(t.Hub.Client.IisOpsSent);
        Assert.False((await pools.DecideAsync(Operator("Prod"), q.RequestId!.Value, true, null)).Ok);
        Assert.True((await pools.DecideAsync(Operator("Prod", "op2"), q.RequestId.Value, true, "ok")).Ok);
        var sent = t.Hub.Client.IisOpsSent.Select(s => s.Command).ToList();
        Assert.Equal(5, sent.Count);
        Assert.Equal(701, sent.Single(s => s.Op == IisOp.CreateApplication).SiteId);
        await using var db = t.Db.CreateDbContext();
        Assert.Single(await db.PoolChangeRequests.ToListAsync());
    }

    [Fact]
    public async Task An_owner_can_apply_a_template_in_dev_but_not_one_with_modify_and_not_in_production()
    {
        await Setup();
        var plain = (await svc.CreateAsync(Operator(), "Plain", null, Def())).Value;
        var modify = (await svc.CreateAsync(Operator(), "WithModify", null, Def(modify: true))).Value;
        var bad = await svc.ApplyAsync(Owner(), serverId, modify, Input(), projectId, "reason");
        Assert.False(bad.Ok); Assert.Contains("Modify", bad.Error);
        Assert.Empty(t.Hub.Client.IisOpsSent);
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ApplyAsync(t.Nobody, serverId, plain, Input(), projectId, null));
    }

    [Fact]
    public async Task Capture_reads_a_site_from_discovery()
    {
        await Setup();
        var r = await svc.CaptureAsync(Operator(), serverId, 1, "From pay", null);
        Assert.True(r.Ok, r.Error);
        Assert.False((await svc.CaptureAsync(Operator(), serverId, 999, "x", null)).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.CaptureAsync(Owner(), serverId, 1, "y", null));
    }
}
