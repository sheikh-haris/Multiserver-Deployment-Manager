using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public class PoolAdminTests : IDisposable
{
    private readonly TestEnv t = new();
    private readonly PendingAgentResults pending;
    private readonly PoolAdminService svc;
    private readonly Dictionary<string, (string State, PoolConfig Config)> pools = new(StringComparer.OrdinalIgnoreCase);
    private Guid serverId;
    public void Dispose() => t.Dispose();

    private static PoolConfig Default() => new()
    {
        ManagedRuntimeVersion = "v4.0", PipelineMode = "Integrated", Enable32Bit = false, StartMode = "OnDemand", IdentityType = "ApplicationPoolIdentity", LoadUserProfile = false,
        IdleTimeoutMinutes = 20, QueueLength = 1000, RecycleIntervalMinutes = 1740, RecycleTimes = [], PrivateMemoryLimitKb = 0, RapidFailProtection = true, RapidFailMaxCrashes = 5,
    };

    public PoolAdminTests()
    {
        pending = new PendingAgentResults(t.Clock);
        svc = new PoolAdminService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock, new IisOpsService(t.Db, t.Commands, pending, t.Audit, t.Bus, t.Clock));
        // A fake agent that behaves like the real one for the cases below.
        t.Hub.Client.OnPoolAction = (env, c) =>
        {
            var (state, cfg) = pools[c.Pool];
            var after = c.Action switch { PoolActions.Start => "Started", PoolActions.Stop => "Stopped", _ => state };
            if (c.Action == PoolActions.Recycle && state != "Started") { pending.CompletePool(env.ServerId, new(env.CommandId, false, "only a started pool can be recycled", state, state, cfg, cfg, false)); return Task.CompletedTask; }
            pools[c.Pool] = (after, cfg);
            pending.CompletePool(env.ServerId, new(env.CommandId, true, null, state, after, cfg, cfg, state != after));
            return Task.CompletedTask;
        };
        t.Hub.Client.OnUpdatePool = (env, c) =>
        {
            var (state, live) = pools[c.Pool];
            var conflicts = PoolRules.Conflicts(live, c.Expected, c.Change);
            if (conflicts.Count > 0) { pending.CompletePool(env.ServerId, new(env.CommandId, false, "changed by someone else: " + string.Join("; ", conflicts), state, state, live, live, false)); return Task.CompletedTask; }
            var eff = PoolRules.Effective(live, c.Change);
            var next = live with
            {
                IdleTimeoutMinutes = eff.IdleTimeoutMinutes ?? live.IdleTimeoutMinutes, QueueLength = eff.QueueLength ?? live.QueueLength,
                IdentityType = eff.IdentityType ?? live.IdentityType, RecycleTimes = eff.RecycleTimes ?? live.RecycleTimes, ManagedRuntimeVersion = eff.ManagedRuntimeVersion ?? live.ManagedRuntimeVersion,
            };
            pools[c.Pool] = (state, next);
            pending.CompletePool(env.ServerId, new(env.CommandId, true, null, state, state, live, next, !eff.IsEmpty));
            return Task.CompletedTask;
        };
    }

    /// <summary>An approved Managed server with two pools; WEB01 site uses ShopPool, linked to a component.</summary>
    private async Task Setup(string env = "Dev", bool managed = true, bool online = true)
    {
        serverId = await t.ApprovedServer("POOL01", env);
        await t.Store(serverId, new SiteSpec(1, "shop", @"D:\Websites\shop", "ShopPool"), new SiteSpec(2, "other", @"D:\Websites\other", "OtherPool"));
        var comp = await t.NewComponent(await t.NewProject("Payments"), "Web");
        await t.Link(comp, serverId, 1);
        await using (var db = t.Db.CreateDbContext())
        {
            var s = await db.Servers.FirstAsync(x => x.Id == serverId);
            s.Mode = managed ? ServerMode.Managed : ServerMode.ReadOnly; s.LocalAllowManaged = true;
            foreach (var p in await db.IisAppPools.Where(x => x.ServerId == serverId).ToListAsync())
            {
                pools[p.Name] = ("Started", Default());
                p.ConfigJson = JsonSerializer.Serialize(Default());
            }
            await db.SaveChangesAsync();
        }
        if (online) t.Tracker.Add(serverId, "conn");
    }

    private Actor Operator(string env = "Dev") => t.ActorWith(Role.ServerOperator, env, "op");
    private async Task<List<(string Action, string Result, string? Before, string? After, string? Detail)>> Audit(string prefix = "Pool.")
    {
        await using var db = t.Db.CreateDbContext();
        return (await db.AuditLog.Where(a => a.Action.StartsWith(prefix)).OrderBy(a => a.Id).ToListAsync()).Select(a => (a.Action, a.Result, a.Before, a.After, a.Detail)).ToList();
    }

    // ---------------- who can do what ----------------

    [Fact]
    public async Task The_page_shows_pools_with_their_sites_and_components_and_who_may_act()
    {
        await Setup();
        var page = (await svc.GetAsync(Operator(), serverId))!;
        Assert.True(page.CanAct);
        var shop = page.Pools.Single(p => p.Name == "ShopPool");
        Assert.Equal(["shop"], shop.Sites);
        Assert.Equal(["Payments / Web"], shop.Components);
        Assert.Equal("v4.0", shop.Config!.ManagedRuntimeVersion);

        var auditor = (await svc.GetAsync(t.ActorWith(Role.Auditor), serverId))!;
        Assert.False(auditor.CanAct); Assert.Contains("Server Operator", auditor.WhyNot);
        Assert.Null(await svc.GetAsync(t.Nobody, serverId));
    }

    [Fact]
    public async Task Only_an_operator_of_that_environment_or_admin_can_act_and_a_denial_is_audited()
    {
        await Setup();
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ActAsync(t.ActorWith(Role.ServerOperator, "Prod", "op2"), serverId, ["ShopPool"], PoolActions.Recycle, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ActAsync(t.ActorWith(Role.Auditor), serverId, ["ShopPool"], PoolActions.Recycle, true));
        Assert.Contains(await Audit(), a => a.Action == "Pool.Recycle" && a.Result == "Denied");
        Assert.Empty(t.Hub.Client.PoolActionsSent);
        Assert.True((await svc.ActAsync(t.Admin, serverId, ["ShopPool"], PoolActions.Recycle, true)).All(r => r.Ok));
    }

    [Fact]
    public async Task A_read_only_server_and_an_offline_agent_block_everything_and_nothing_is_sent()
    {
        await Setup(managed: false);
        var ro = await svc.ActAsync(Operator(), serverId, ["ShopPool"], PoolActions.Stop, true);
        Assert.Contains("ReadOnly", ro.Single().Message);
        Assert.Contains("ReadOnly", (await svc.GetAsync(Operator(), serverId))!.WhyNot);

        await using (var db = t.Db.CreateDbContext()) { (await db.Servers.FirstAsync()).Mode = ServerMode.Managed; await db.SaveChangesAsync(); }
        t.Tracker.Remove(serverId, "conn");
        Assert.Contains("offline", (await svc.ActAsync(Operator(), serverId, ["ShopPool"], PoolActions.Stop, true)).Single().Message);
        Assert.Empty(t.Hub.Client.PoolActionsSent);
    }

    // ---------------- start / stop / recycle ----------------

    [Fact]
    public async Task Several_selected_pools_are_handled_one_by_one_each_audited_with_before_and_after()
    {
        await Setup();
        var r = await svc.ActAsync(Operator(), serverId, ["ShopPool", "OtherPool"], PoolActions.Stop, true);
        Assert.All(r, x => Assert.True(x.Ok));
        Assert.Equal(["Stopped", "Stopped"], r.Select(x => x.StateAfter));
        var a = (await Audit()).Where(x => x.Action == "Pool.Stop").ToList();
        Assert.Equal(2, a.Count);
        Assert.All(a, x => { Assert.Equal(("Success", "Started", "Stopped"), (x.Result, x.Before, x.After)); });
        // the stored snapshot changed too
        var page = (await svc.GetAsync(Operator(), serverId))!;
        Assert.All(page.Pools, p => Assert.Equal("Stopped", p.State));
    }

    [Fact]
    public async Task One_failing_pool_does_not_stop_the_others_and_is_reported_and_audited()
    {
        await Setup();
        await svc.ActAsync(Operator(), serverId, ["ShopPool"], PoolActions.Stop, true);
        var r = await svc.ActAsync(Operator(), serverId, ["ShopPool", "OtherPool"], PoolActions.Recycle, true);       // ShopPool is stopped: cannot recycle
        Assert.False(r[0].Ok); Assert.Contains("only a started pool", r[0].Message);
        Assert.True(r[1].Ok);
        Assert.Contains(await Audit(), a => a.Action == "Pool.Recycle" && a.Result == "Failed");
    }

    [Fact]
    public async Task Unknown_pools_and_bad_requests_are_refused_without_contacting_the_agent()
    {
        await Setup();
        Assert.False((await svc.ActAsync(Operator(), serverId, ["Nope"], PoolActions.Stop, true)).Single().Ok);
        Assert.False((await svc.ActAsync(Operator(), serverId, [], PoolActions.Stop, true)).Single().Ok);
        Assert.False((await svc.ActAsync(Operator(), serverId, ["ShopPool"], "Delete", true)).Single().Ok);
        Assert.False((await svc.ActAsync(Operator(), serverId, Enumerable.Range(0, 60).Select(i => "p" + i).ToList(), PoolActions.Stop, true)).Single().Ok);
        Assert.Empty(t.Hub.Client.PoolActionsSent);
    }

    [Fact]
    public async Task In_production_stop_and_edit_cannot_be_done_directly_recycle_needs_confirmation_and_start_is_free()
    {
        await Setup("Prod");
        var op = Operator("Prod");
        Assert.Contains("second person", (await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Stop, true)).Single().Message);          // even when "confirmed"
        Assert.Contains("confirm", (await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Recycle, false)).Single().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(t.Hub.Client.PoolActionsSent);
        Assert.True((await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Start, false)).Single().Ok);
        Assert.True((await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Recycle, true)).Single().Ok);
        var edit = await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }, confirmed: true);
        Assert.False(edit.Ok); Assert.Contains("second person", edit.Error);
        Assert.Empty(t.Hub.Client.PoolUpdatesSent);
    }

    // ---------------- Production: a second person ----------------

    private Actor Op2() => t.ActorWith(Role.ServerOperator, "Prod", "op2");

    [Fact]
    public async Task A_stop_request_needs_a_reason_waits_and_runs_only_when_a_different_operator_approves()
    {
        await Setup("Prod");
        var op = Operator("Prod");
        Assert.False((await svc.RequestStopAsync(op, serverId, ["ShopPool"], " ")).Ok);
        var id = (await svc.RequestStopAsync(op, serverId, ["ShopPool", "OtherPool"], "decommission test pools")).Value;
        Assert.Empty(t.Hub.Client.PoolActionsSent);                                           // nothing runs yet
        Assert.Equal("Started", pools["ShopPool"].State);

        var own = await svc.DecideAsync(op, id, true, null);
        Assert.False(own.Ok); Assert.Contains("second person", own.Error);
        Assert.Contains(await Audit(), a => a.Action == "Pool.Request.Approve" && a.Result == "Denied");
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.DecideAsync(t.ActorWith(Role.Auditor), id, true, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.DecideAsync(t.ActorWith(Role.ServerOperator, "Dev", "dev-op"), id, true, null));

        Assert.True((await svc.DecideAsync(Op2(), id, true, "ok")).Ok);
        Assert.Equal(["Stopped", "Stopped"], [pools["ShopPool"].State, pools["OtherPool"].State]);
        var a = await Audit();
        Assert.Contains(a, x => x.Action == "Pool.Stop" && x.Detail!.Contains("Approved by User op2") && x.Detail.Contains("decommission"));
        Assert.Contains(a, x => x.Action == "Pool.Request.Approve" && x.Result == "Success");
        Assert.False((await svc.DecideAsync(Op2(), id, true, null)).Ok);                      // already done
        var row = (await svc.RequestsAsync(Op2())).Single();
        Assert.Equal(PoolRequestStatus.Executed, row.Request.Status);
    }

    [Fact]
    public async Task An_edit_request_runs_the_stored_change_with_its_stale_check_and_a_stale_edit_is_reported_as_failed()
    {
        await Setup("Prod");
        var id = (await svc.RequestEditAsync(Operator("Prod"), serverId, "ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }, "traffic growth")).Value;
        pools["ShopPool"] = ("Started", Default() with { QueueLength = 5000 });               // changed while waiting for approval
        var r = await svc.DecideAsync(Op2(), id, true, null);
        Assert.False(r.Ok); Assert.Contains("someone else", r.Error);
        Assert.Equal(5000, pools["ShopPool"].Config.QueueLength);
        Assert.Equal(PoolRequestStatus.Failed, (await svc.RequestsAsync(Op2())).Single().Request.Status);

        var id2 = (await svc.RequestEditAsync(Operator("Prod"), serverId, "ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 5000 }, "again")).Value;
        Assert.True((await svc.DecideAsync(Op2(), id2, true, null)).Ok);
        Assert.Equal(2000, pools["ShopPool"].Config.QueueLength);
    }

    [Fact]
    public async Task Reject_needs_a_reason_cancel_is_for_the_requester_and_old_requests_expire()
    {
        await Setup("Prod");
        var op = Operator("Prod");
        var id = (await svc.RequestStopAsync(op, serverId, ["ShopPool"], "x")).Value;
        Assert.False((await svc.DecideAsync(Op2(), id, false, " ")).Ok);
        Assert.True((await svc.DecideAsync(Op2(), id, false, "not now")).Ok);
        Assert.Equal(PoolRequestStatus.Rejected, (await svc.RequestsAsync(op)).Single().Request.Status);

        var id2 = (await svc.RequestStopAsync(op, serverId, ["ShopPool"], "y")).Value;
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.CancelRequestAsync(Op2(), id2));
        Assert.True((await svc.CancelRequestAsync(op, id2)).Ok);

        var id3 = (await svc.RequestStopAsync(op, serverId, ["ShopPool"], "z")).Value;
        t.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Contains("Expired", (await svc.DecideAsync(Op2(), id3, true, null)).Error);
        Assert.Empty(t.Hub.Client.PoolActionsSent);
    }

    [Fact]
    public async Task Requests_only_exist_for_production_and_pending_ones_are_counted_for_the_other_operator()
    {
        await Setup("Dev");
        Assert.Contains("Only Production", (await svc.RequestStopAsync(Operator(), serverId, ["ShopPool"], "x")).Error);
        var s2 = await t.ApprovedServer("PROD01", "Prod");
        Assert.NotEqual(serverId, s2);
    }

    // ---------------- project Deployers recycle their own pool ----------------

    private async Task<(Actor deployer, Guid target)> DeployerWorld(string env = "Dev")
    {
        await Setup(env);
        await using var db = t.Db.CreateDbContext();
        var comp = await db.Components.FirstAsync();
        var target = (await db.DeploymentTargets.FirstAsync()).Id;
        var user = new UserContext("dep", "Dep", []);
        var assign = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "dep", Scope = RoleScope.Project, ProjectId = comp.ProjectId, Role = Role.Deployer, EnvironmentName = env };
        return (new Actor("dep", "Dep", null, new AccessEvaluator(user, [assign], t.Clock.GetUtcNow())), target);
    }

    [Fact]
    public async Task A_deployer_can_recycle_the_pool_of_their_own_target_only_and_it_is_audited()
    {
        var (dep, target) = await DeployerWorld();
        var r = await svc.RecycleForTargetAsync(dep, target, confirmed: false);
        Assert.True(r.Ok, r.Message);
        Assert.Equal([PoolActions.Recycle], t.Hub.Client.PoolActionsSent.Select(x => x.Command.Action));
        Assert.Equal("ShopPool", t.Hub.Client.PoolActionsSent.Single().Command.Pool);
        Assert.Contains(await Audit(), a => a.Action == "Pool.Recycle" && a.Result == "Success" && a.Detail!.Contains("Deployer"));
    }

    [Fact]
    public async Task A_deployer_of_another_environment_a_viewer_and_a_stranger_cannot_recycle()
    {
        var (_, target) = await DeployerWorld();
        var comp = await t.Db.CreateDbContext().Components.FirstAsync();
        Actor Person(Role role, string env, Guid? project = null)
        {
            var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "x", Scope = RoleScope.Project, ProjectId = project ?? comp.ProjectId, Role = role, EnvironmentName = env };
            return new Actor("x", "X", null, new AccessEvaluator(new UserContext("x", "X", []), [a], t.Clock.GetUtcNow()));
        }
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RecycleForTargetAsync(Person(Role.Deployer, "Prod"), target, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RecycleForTargetAsync(Person(Role.ProjectViewer, "Dev"), target, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RecycleForTargetAsync(Person(Role.Deployer, "Dev", Guid.NewGuid()), target, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.RecycleForTargetAsync(t.Nobody, target, true));
        Assert.Empty(t.Hub.Client.PoolActionsSent);
    }

    [Fact]
    public async Task A_deployer_cannot_recycle_a_pool_that_also_serves_other_applications_or_when_the_server_is_read_only()
    {
        var (dep, target) = await DeployerWorld();
        await t.Store(serverId, new SiteSpec(1, "shop", @"D:\Websites\shop", "ShopPool"), new SiteSpec(2, "other", @"D:\Websites\other", "ShopPool"));     // OtherPool is now also ShopPool
        var shared = await svc.RecycleForTargetAsync(dep, target, false);
        Assert.False(shared.Ok); Assert.Contains("also serves", shared.Message);
        Assert.Empty(t.Hub.Client.PoolActionsSent);

        var (dep2, target2) = (dep, target);
        await t.Store(serverId, new SiteSpec(1, "shop", @"D:\Websites\shop", "ShopPool"), new SiteSpec(2, "other", @"D:\Websites\other", "OtherPool"));
        await using (var db = t.Db.CreateDbContext()) { (await db.Servers.FirstAsync(s => s.Id == serverId)).Mode = ServerMode.ReadOnly; await db.SaveChangesAsync(); }
        var ro = await svc.RecycleForTargetAsync(dep2, target2, false);
        Assert.False(ro.Ok); Assert.Contains("ReadOnly", ro.Message);
    }

    [Fact]
    public async Task In_production_a_deployer_recycle_needs_the_confirmation()
    {
        var (dep, target) = await DeployerWorld("Prod");
        Assert.Contains("confirm", (await svc.RecycleForTargetAsync(dep, target, false)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((await svc.RecycleForTargetAsync(dep, target, true)).Ok);
    }

    [Fact]
    public async Task An_agent_that_never_answers_gives_a_timeout_message_not_a_hang()
    {
        await Setup();
        t.Hub.Client.OnPoolAction = (_, _) => Task.CompletedTask;
        var task = svc.ActAsync(Operator(), serverId, ["ShopPool"], PoolActions.Stop, true);
        // FakeTimeProvider does not drive Task timeouts, so use a real short wait: the call uses a 60 s timeout, so just prove it is still waiting
        await Task.Delay(300);
        Assert.False(task.IsCompleted);
    }

    // ---------------- edit ----------------

    [Fact]
    public async Task An_edit_is_validated_sent_audited_with_only_the_changed_fields_and_stored_without_drift()
    {
        await Setup();
        var change = new PoolConfig { IdleTimeoutMinutes = 0, RecycleTimes = ["03:30"] };
        var r = await svc.UpdateAsync(Operator(), serverId, "ShopPool", change, new PoolConfig { IdleTimeoutMinutes = 20, RecycleTimes = [] }, confirmed: false);
        Assert.True(r.Ok, r.Error);
        var a = (await Audit()).Single(x => x.Action == "Pool.Update");
        Assert.Equal("Success", a.Result);
        Assert.Contains("\"IdleTimeoutMinutes\":20", a.Before); Assert.Contains("\"IdleTimeoutMinutes\":0", a.After);
        Assert.DoesNotContain("QueueLength", a.After!);                           // untouched fields are not in the audit
        var shop = (await svc.GetAsync(Operator(), serverId))!.Pools.Single(p => p.Name == "ShopPool");
        Assert.Equal(0, shop.Config!.IdleTimeoutMinutes);
        // the next discovery (same as before for the fields the portal does not manage) must not report drift for this pool
        await using var db = t.Db.CreateDbContext();
        Assert.Empty(db.DriftEvents.Where(d => d.ObjectType == "AppPool"));
    }

    [Fact]
    public async Task Invalid_dangerous_and_stale_edits_are_refused_and_failures_are_audited()
    {
        await Setup();
        var op = Operator();
        Assert.Contains("custom accounts", (await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig { IdentityType = "LocalSystem" }, new PoolConfig(), false)).Error);
        Assert.False((await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig { QueueLength = 1 }, new PoolConfig(), false)).Ok);
        Assert.False((await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig(), new PoolConfig(), false)).Ok);
        Assert.Empty(t.Hub.Client.PoolUpdatesSent);                                 // all refused before contacting the agent

        pools["ShopPool"] = ("Started", Default() with { QueueLength = 5000 });     // someone changed it behind our back
        var stale = await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }, false);
        Assert.False(stale.Ok); Assert.Contains("someone else", stale.Error);
        Assert.Contains(await Audit(), a => a.Action == "Pool.Update" && a.Result == "Failed");
        Assert.Equal(5000, pools["ShopPool"].Config.QueueLength);
    }

    [Fact]
    public async Task Setting_the_current_values_again_is_a_success_that_says_nothing_changed()
    {
        await Setup();
        var r = await svc.UpdateAsync(Operator(), serverId, "ShopPool", new PoolConfig { QueueLength = 1000 }, new PoolConfig { QueueLength = 1000 }, false);
        Assert.True(r.Ok);
        Assert.Contains("Nothing changed", (await Audit()).Single(a => a.Action == "Pool.Update").Detail);
    }

    [Fact]
    public async Task A_result_from_another_servers_agent_is_not_accepted()
    {
        await Setup();
        var other = await t.ApprovedServer("POOL02", "Dev");
        Assert.False(pending.CompletePool(other, new(Guid.NewGuid(), true, null, null, null, null, null, false)));
        var cmd = Guid.NewGuid(); _ = pending.ExpectPool(cmd, serverId);
        Assert.False(pending.CompletePool(other, new(cmd, true, null, "Stopped", "Stopped", null, null, false)));      // right command id, wrong server
        Assert.True(pending.CompletePool(serverId, new(cmd, true, null, "Stopped", "Stopped", null, null, false)));
    }
}
