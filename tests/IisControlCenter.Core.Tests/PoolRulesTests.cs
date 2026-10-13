using IisControlCenter.Contracts;

namespace IisControlCenter.Core.Tests;

public class PoolRulesTests
{
    [Fact] public void An_empty_change_is_refused() => Assert.Contains("Nothing to change.", PoolRules.Validate(new PoolConfig()));

    [Theory]
    [InlineData("ApplicationPoolIdentity", true)]
    [InlineData("NetworkService", true)]
    [InlineData("LocalService", true)]
    [InlineData("LocalSystem", false)]
    [InlineData("SpecificUser", false)]
    [InlineData("", false)]
    public void Identity_allow_list(string id, bool ok) => Assert.Equal(ok, PoolRules.Validate(new PoolConfig { IdentityType = id }).Count == 0);

    [Theory]
    [InlineData(0, true)] [InlineData(1440, true)] [InlineData(-1, false)] [InlineData(1441, false)]
    public void Idle_timeout_range(int v, bool ok) => Assert.Equal(ok, PoolRules.Validate(new PoolConfig { IdleTimeoutMinutes = v }).Count == 0);

    [Theory]
    [InlineData(10, true)] [InlineData(65535, true)] [InlineData(9, false)] [InlineData(65536, false)]
    public void Queue_length_range(int v, bool ok) => Assert.Equal(ok, PoolRules.Validate(new PoolConfig { QueueLength = v }).Count == 0);

    [Theory]
    [InlineData("", true)] [InlineData("v4.0", true)] [InlineData("v2.0", true)] [InlineData("v9.9", false)] [InlineData("v4.0; calc", false)]
    public void Runtime_allow_list(string v, bool ok) => Assert.Equal(ok, PoolRules.Validate(new PoolConfig { ManagedRuntimeVersion = v }).Count == 0);

    [Fact]
    public void Recycle_times_must_be_valid_and_unique()
    {
        Assert.Empty(PoolRules.Validate(new PoolConfig { RecycleTimes = ["03:30", "23:59", "00:00"] }));
        Assert.NotEmpty(PoolRules.Validate(new PoolConfig { RecycleTimes = ["24:00"] }));
        Assert.NotEmpty(PoolRules.Validate(new PoolConfig { RecycleTimes = ["3.30"] }));
        Assert.NotEmpty(PoolRules.Validate(new PoolConfig { RecycleTimes = ["03:30", "03:30"] }));
        Assert.NotEmpty(PoolRules.Validate(new PoolConfig { RecycleTimes = Enumerable.Range(0, 25).Select(i => $"{i % 24:00}:{(i / 24) * 10:00}").ToList() }));
    }

    [Theory]
    [InlineData("DefaultAppPool", true)] [InlineData("My Pool-1.x", true)] [InlineData("", false)] [InlineData("a\\b", false)] [InlineData("a/b", false)] [InlineData("a\nb", false)]
    public void Pool_names(string n, bool ok) => Assert.Equal(ok, PoolRules.ValidateName(n) is null);

    [Fact]
    public void Effective_keeps_only_real_differences()
    {
        var live = new PoolConfig { QueueLength = 1000, IdleTimeoutMinutes = 20, ManagedRuntimeVersion = "v4.0", RecycleTimes = ["03:00"] };
        var e = PoolRules.Effective(live, new PoolConfig { QueueLength = 1000, IdleTimeoutMinutes = 0, ManagedRuntimeVersion = "v4.0", RecycleTimes = ["03:00"] });
        Assert.Equal(new PoolConfig { IdleTimeoutMinutes = 0 }, e);
        Assert.True(PoolRules.Effective(live, new PoolConfig { QueueLength = 1000 }).IsEmpty);
    }

    [Fact]
    public void Conflicts_only_look_at_the_fields_being_changed()
    {
        var live = new PoolConfig { QueueLength = 5000, IdleTimeoutMinutes = 20 };
        Assert.Empty(PoolRules.Conflicts(live, new PoolConfig { IdleTimeoutMinutes = 20 }, new PoolConfig { IdleTimeoutMinutes = 0 }));          // queue changed, but we do not touch it
        Assert.Single(PoolRules.Conflicts(live, new PoolConfig { QueueLength = 1000 }, new PoolConfig { QueueLength = 2000 }));
    }
}
