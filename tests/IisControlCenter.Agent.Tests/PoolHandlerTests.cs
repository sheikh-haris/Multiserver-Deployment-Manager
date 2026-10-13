using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Pools;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IisControlCenter.Agent.Tests;

public class PoolHandlerTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly SamplePoolAdmin admin = new();

    private PoolHandler Handler(bool allowManaged = true) =>
        new(new CommandPolicy(new AgentConfig { PortalUrl = "https://agents.local", Environment = "Dev", AllowedRoots = [@"d:\websites"], ServerId = Me, AllowManaged = allowManaged }, clock), admin, NullLogger<PoolHandler>.Instance);

    private CommandEnvelope Env(ServerMode mode = ServerMode.Managed, string env = "Dev") =>
        new(Guid.NewGuid(), Me, env, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "c", mode);

    private PoolConfig Live(string pool = "ShopPool") => admin.Get(pool)!.Config;

    // ---------------- start / stop / recycle ----------------

    [Fact]
    public async Task Stop_start_and_recycle_work_and_report_before_and_after()
    {
        var h = Handler();
        var stop = await h.ActAsync(Env(), new(  "ShopPool", PoolActions.Stop));
        Assert.True(stop.Ok); Assert.Equal(("Started", "Stopped", true), (stop.StateBefore, stop.StateAfter, stop.Changed));
        var recycleStopped = await h.ActAsync(Env(), new("ShopPool", PoolActions.Recycle));
        Assert.False(recycleStopped.Ok); Assert.Contains("only a started pool", recycleStopped.Error);
        var start = await h.ActAsync(Env(), new("ShopPool", PoolActions.Start));
        Assert.Equal(("Stopped", "Started"), (start.StateBefore, start.StateAfter));
        var recycle = await h.ActAsync(Env(), new("ShopPool", PoolActions.Recycle));
        Assert.True(recycle.Ok); Assert.False(recycle.Changed);
    }

    [Fact]
    public async Task A_read_only_server_refuses_every_pool_command_even_if_the_portal_says_Managed()
    {
        var local = Handler(allowManaged: false);                            // the agent was installed without Managed mode
        var r = await local.ActAsync(Env(ServerMode.Managed), new("ShopPool", PoolActions.Stop));
        Assert.False(r.Ok); Assert.Contains("ReadOnly", r.Error);
        Assert.Equal("Started", admin.Get("ShopPool")!.State);

        var portalReadOnly = await Handler().ActAsync(Env(ServerMode.ReadOnly), new("ShopPool", PoolActions.Stop));
        Assert.False(portalReadOnly.Ok);
        var upd = await local.UpdateAsync(Env(), new("ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }));
        Assert.False(upd.Ok);
    }

    [Fact]
    public async Task Wrong_environment_wrong_server_expired_duplicate_unknown_pool_and_bad_names_are_refused()
    {
        var h = Handler();
        Assert.False((await h.ActAsync(Env(env: "Prod"), new("ShopPool", PoolActions.Stop))).Ok);
        var dup = Env(); Assert.True((await h.ActAsync(dup, new("ShopPool", PoolActions.Recycle))).Ok);
        Assert.Contains("Duplicate", (await h.ActAsync(dup, new("ShopPool", PoolActions.Recycle))).Error);
        var expired = Env(); clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Contains("expired", (await h.ActAsync(expired, new("ShopPool", PoolActions.Recycle))).Error);
        Assert.Contains("does not exist", (await h.ActAsync(Env(), new("NoSuchPool", PoolActions.Stop))).Error);
        Assert.False((await h.ActAsync(Env(), new("..\\x", PoolActions.Stop))).Ok);
        Assert.False((await h.ActAsync(Env(), new("ShopPool", "Delete"))).Ok);
        Assert.False((await h.ActAsync(Env(), new("", PoolActions.Stop))).Ok);
    }

    // ---------------- edit ----------------

    [Fact]
    public async Task An_edit_changes_only_the_asked_fields_and_returns_full_before_and_after()
    {
        var h = Handler();
        var before = Live();
        var r = await h.UpdateAsync(Env(), new("ShopPool", new PoolConfig { IdleTimeoutMinutes = 0, RecycleTimes = ["03:30", "15:00"], Enable32Bit = true }, new PoolConfig { IdleTimeoutMinutes = 20, RecycleTimes = [], Enable32Bit = false }));
        Assert.True(r.Ok, r.Error); Assert.True(r.Changed);
        var after = Live();
        Assert.Equal((0, true), (after.IdleTimeoutMinutes, after.Enable32Bit));
        Assert.Equal(["03:30", "15:00"], after.RecycleTimes);
        Assert.Equal(before with { IdleTimeoutMinutes = 0, RecycleTimes = after.RecycleTimes, Enable32Bit = true }, after);          // nothing else moved
        Assert.Equal(20, r.Before!.IdleTimeoutMinutes); Assert.Equal(0, r.After!.IdleTimeoutMinutes);
    }

    [Fact]
    public async Task Setting_a_value_to_what_it_already_is_changes_nothing()
    {
        var r = await Handler().UpdateAsync(Env(), new("ShopPool", new PoolConfig { QueueLength = 1000 }, new PoolConfig { QueueLength = 1000 }));
        Assert.True(r.Ok); Assert.False(r.Changed);
    }

    [Fact]
    public async Task A_stale_edit_is_refused_when_someone_changed_the_pool_after_the_person_opened_it()
    {
        var h = Handler();
        admin.Apply("ShopPool", new PoolConfig { QueueLength = 5000 });         // changed behind the portal's back
        var r = await h.UpdateAsync(Env(), new("ShopPool", new PoolConfig { QueueLength = 2000 }, new PoolConfig { QueueLength = 1000 }));
        Assert.False(r.Ok); Assert.Contains("changed by someone else", r.Error);
        Assert.Equal(5000, Live().QueueLength);
    }

    [Theory]
    [InlineData("LocalSystem")]
    [InlineData("SpecificUser")]
    public async Task Dangerous_identities_cannot_be_set_from_the_portal(string identity)
    {
        var r = await Handler().UpdateAsync(Env(), new("ShopPool", new PoolConfig { IdentityType = identity }, new PoolConfig { IdentityType = "ApplicationPoolIdentity" }));
        Assert.False(r.Ok); Assert.Contains("custom accounts", r.Error);
        Assert.Equal("ApplicationPoolIdentity", Live().IdentityType);
    }

    [Fact]
    public async Task Invalid_values_are_refused_before_anything_is_touched()
    {
        var r = await Handler().UpdateAsync(Env(), new("ShopPool", new PoolConfig { QueueLength = 3, IdleTimeoutMinutes = -1, RecycleTimes = ["25:99"] }, new PoolConfig()));
        Assert.False(r.Ok);
        Assert.Equal(1000, Live().QueueLength);
    }
}
