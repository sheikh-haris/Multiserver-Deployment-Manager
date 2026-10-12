using IisControlCenter.Core.Discovery;
using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Tests;

public class DiscoveryReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid S1 = Guid.NewGuid(), S2 = Guid.NewGuid();

    private static IisSite Site(Guid server, string name, string pool, MappingStatus m = MappingStatus.Unassigned, params (string path, string pool)[] extra) => new()
    {
        ServerId = server, Name = name, AppPoolName = pool, MappingStatus = m,
        Applications = new[] { ("/", pool) }.Concat(extra).Select(a => new IisApplication { Path = a.Item1, AppPoolName = a.Item2 }).ToList(),
    };

    [Fact]
    public void Lists_only_unassigned_sites()
    {
        var r = DiscoveryReportBuilder.Build([Site(S1, "a", "p1"), Site(S1, "b", "p2", MappingStatus.Mapped)], [], [], Now);
        Assert.Equal(["a"], r.UnassignedSites.Select(s => s.Name));
    }

    [Fact]
    public void Detects_pool_shared_by_two_sites_or_a_site_and_its_sub_application()
    {
        var r = DiscoveryReportBuilder.Build(
            [Site(S1, "a", "shared"), Site(S1, "b", "shared"), Site(S1, "c", "own", MappingStatus.Unassigned, ("/api", "own")), Site(S1, "d", "p4", MappingStatus.Unassigned, ("/x", "p5"))],
            [], [], Now);
        // "shared": two sites. "own": site c and its sub-application /api are two applications on one pool.
        Assert.Equal(["own", "shared"], r.SharedPools.Select(p => p.PoolName).OrderBy(x => x));
        Assert.Equal(["a", "b"], r.SharedPools.Single(p => p.PoolName == "shared").UsedBy);
        Assert.Equal(["c", "c/api"], r.SharedPools.Single(p => p.PoolName == "own").UsedBy);
    }

    [Fact]
    public void Same_pool_name_on_different_servers_is_not_shared()
    {
        var r = DiscoveryReportBuilder.Build([Site(S1, "a", "DefaultAppPool"), Site(S2, "b", "DefaultAppPool")], [], [], Now);
        Assert.Empty(r.SharedPools);
    }

    [Fact]
    public void Pool_matching_ignores_case()
    {
        var r = DiscoveryReportBuilder.Build([Site(S1, "a", "Pool"), Site(S1, "b", "POOL")], [], [], Now);
        Assert.Single(r.SharedPools);
    }

    [Fact]
    public void Finds_local_system_pools_only()
    {
        var r = DiscoveryReportBuilder.Build([], [new IisAppPool { ServerId = S1, Name = "x", IdentityType = "LocalSystem" }, new IisAppPool { ServerId = S1, Name = "y", IdentityType = "ApplicationPoolIdentity" }], [], Now);
        Assert.Equal(["x"], r.LocalSystemPools.Select(p => p.Name));
    }

    [Theory]
    [InlineData(-5, true)]    // already expired
    [InlineData(0, true)]
    [InlineData(30, true)]    // exactly 30 days: included
    [InlineData(31, false)]
    [InlineData(400, false)]
    public void Certificate_window_is_30_days(int daysLeft, bool included)
    {
        var c = new IisCertificate { ServerId = S1, Thumbprint = "t", Subject = "s", NotAfter = Now.AddDays(daysLeft) };
        var r = DiscoveryReportBuilder.Build([], [], [c], Now);
        Assert.Equal(included, r.ExpiringCertificates.Count == 1);
    }

    [Fact]
    public void Expiring_certificates_are_sorted_soonest_first()
    {
        var r = DiscoveryReportBuilder.Build([], [], [new IisCertificate { NotAfter = Now.AddDays(20), Thumbprint = "b" }, new IisCertificate { NotAfter = Now.AddDays(2), Thumbprint = "a" }], Now);
        Assert.Equal(["a", "b"], r.ExpiringCertificates.Select(c => c.Thumbprint));
    }
}
