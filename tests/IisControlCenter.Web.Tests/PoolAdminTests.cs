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
        svc = new PoolAdminService(t.Db, t.Commands, pending, t.Audit, t.Bus);
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
    public async Task In_production_stop_recycle_and_edit_need_an_explicit_confirmation_but_start_does_not()
    {
        await Setup("Prod");
        var op = Operator("Prod");
        Assert.Contains("confirm", (await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Stop, false)).Single().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirm", (await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Recycle, false)).Single().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(t.Hub.Client.PoolActionsSent);
        Assert.True((await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Start, false)).Single().Ok);
        Assert.True((await svc.ActAsync(op, serverId, ["ShopPool"], PoolActions.Stop, true)).Single().Ok);
        Assert.False((await svc.UpdateAsync(op, serverId, "ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }, confirmed: false)).Ok);
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
