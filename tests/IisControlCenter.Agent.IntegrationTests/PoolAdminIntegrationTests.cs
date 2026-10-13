using System.Runtime.Versioning;
using System.Text.Json;
using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Pools;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.Administration;
using Xunit;

namespace IisControlCenter.Agent.IntegrationTests;

/// <summary>Start, stop, recycle and edit a real app pool on a real IIS.</summary>
[SupportedOSPlatform("windows")]
public class PoolAdminIntegrationTests : IClassFixture<IisFixture>
{
    private readonly IisFixture fx;
    public PoolAdminIntegrationTests(IisFixture fx) => this.fx = fx;
    private readonly IisPoolAdmin admin = new();

    private void Need() => Skip.IfNot(fx.Available, fx.SkipReason);

    [SkippableFact]
    public void Reads_the_settings_the_portal_may_change_and_never_a_password_or_user_name()
    {
        Need();
        var web = admin.Get(fx.PoolName)!;
        Assert.Equal("v4.0", web.Config.ManagedRuntimeVersion);
        Assert.Equal("Integrated", web.Config.PipelineMode);
        Assert.Equal("ApplicationPoolIdentity", web.Config.IdentityType);

        var custom = admin.Get(fx.CustomPoolName)!;
        Assert.Equal("SpecificUser", custom.Config.IdentityType);
        var json = JsonSerializer.Serialize(custom);
        Assert.DoesNotContain(IisFixture.Secret, json);
        Assert.DoesNotContain("icc-it-nobody", json);
        Assert.Null(admin.Get("icc-it-does-not-exist"));
    }

    [SkippableFact]
    public void Changes_round_trip_through_real_IIS()
    {
        Need();
        var before = admin.Get(fx.PoolName)!.Config;
        admin.Apply(fx.PoolName, new PoolConfig
        {
            QueueLength = 2222, IdleTimeoutMinutes = 0, RecycleIntervalMinutes = 0, RecycleTimes = ["03:30", "15:00"], PrivateMemoryLimitKb = 512 * 1024,
            Enable32Bit = true, StartMode = "AlwaysRunning", LoadUserProfile = true, RapidFailProtection = true, RapidFailMaxCrashes = 7, ManagedRuntimeVersion = "v2.0", PipelineMode = "Classic",
        });
        var after = admin.Get(fx.PoolName)!.Config;
        Assert.Equal((2222, 0, 0), (after.QueueLength, after.IdleTimeoutMinutes, after.RecycleIntervalMinutes));
        Assert.Equal(["03:30", "15:00"], after.RecycleTimes);
        Assert.Equal((512 * 1024L, true, "AlwaysRunning", true, 7), (after.PrivateMemoryLimitKb, after.Enable32Bit, after.StartMode, after.LoadUserProfile, after.RapidFailMaxCrashes));
        Assert.Equal(("v2.0", "Classic"), (after.ManagedRuntimeVersion, after.PipelineMode));

        admin.Apply(fx.PoolName, before with { RecycleTimes = [] });                    // put it back
        Assert.Equal("v4.0", admin.Get(fx.PoolName)!.Config.ManagedRuntimeVersion);
        Assert.Empty(admin.Get(fx.PoolName)!.Config.RecycleTimes!);
    }

    [SkippableFact]
    public void Editing_a_pool_with_a_custom_account_keeps_the_account_and_its_password()
    {
        Need();
        admin.Apply(fx.CustomPoolName, new PoolConfig { QueueLength = 1500 });
        using var mgr = new ServerManager();
        var p = mgr.ApplicationPools[fx.CustomPoolName];
        Assert.Equal(ProcessModelIdentityType.SpecificUser, p.ProcessModel.IdentityType);
        Assert.Equal(@".\icc-it-nobody", p.ProcessModel.UserName);
        Assert.Equal(IisFixture.Secret, p.ProcessModel.Password);
        Assert.Equal(1500, p.QueueLength);
    }

    [SkippableFact]
    public void Stop_start_and_recycle_work_and_a_stopped_pool_cannot_be_recycled()
    {
        Need();
        Assert.Equal("Started", admin.Act(fx.PoolName, PoolActions.Start));
        Assert.Equal("Started", admin.Act(fx.PoolName, PoolActions.Recycle));
        Assert.Equal("Stopped", admin.Act(fx.PoolName, PoolActions.Stop));
        Assert.Equal("Stopped", admin.Act(fx.PoolName, PoolActions.Stop));               // again: nothing to do
        Assert.Throws<InvalidOperationException>(() => admin.Act(fx.PoolName, PoolActions.Recycle));
        Assert.Equal("Started", admin.Act(fx.PoolName, PoolActions.Start));
    }

    [SkippableFact]
    public async Task The_handler_enforces_its_rules_on_real_IIS()
    {
        Need();
        var me = Guid.NewGuid();
        var clock = TimeProvider.System;
        var cfg = new AgentConfig { PortalUrl = "https://agents.local", Environment = "Dev", AllowedRoots = [@"c:\websites"], ServerId = me, AllowManaged = true };
        var h = new PoolHandler(new CommandPolicy(cfg, clock), admin, NullLogger<PoolHandler>.Instance);
        CommandEnvelope Env(ServerMode m = ServerMode.Managed) => new(Guid.NewGuid(), me, "Dev", clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(5), "c", m);

        Assert.False((await h.ActAsync(Env(ServerMode.ReadOnly), new(fx.PoolName, PoolActions.Recycle))).Ok);
        var live = admin.Get(fx.PoolName)!.Config;
        var stale = await h.UpdateAsync(Env(), new(fx.PoolName, new PoolConfig { QueueLength = 3000 }, new PoolConfig { QueueLength = (live.QueueLength ?? 0) + 1 }));
        Assert.False(stale.Ok); Assert.Contains("someone else", stale.Error);
        var system = await h.UpdateAsync(Env(), new(fx.PoolName, new PoolConfig { IdentityType = "LocalSystem" }, new PoolConfig { IdentityType = live.IdentityType }));
        Assert.False(system.Ok);
        var ok = await h.UpdateAsync(Env(), new(fx.PoolName, new PoolConfig { QueueLength = 3000 }, new PoolConfig { QueueLength = live.QueueLength }));
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(3000, admin.Get(fx.PoolName)!.Config.QueueLength);
        Assert.Equal(live.QueueLength, ok.Before!.QueueLength);
    }
}
