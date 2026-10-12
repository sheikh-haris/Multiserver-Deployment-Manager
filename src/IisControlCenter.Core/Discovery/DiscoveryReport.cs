using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Discovery;

public sealed record ReportSite(Guid ServerId, string Name, string? PhysicalPath, string? AppPoolName);
public sealed record SharedPool(Guid ServerId, string PoolName, IReadOnlyList<string> UsedBy);
public sealed record ReportPool(Guid ServerId, string Name, string IdentityType);
public sealed record ReportCert(Guid ServerId, string Thumbprint, string Subject, DateTimeOffset NotAfter, int DaysLeft);

public sealed record DiscoveryReport(
    IReadOnlyList<ReportSite> UnassignedSites,
    IReadOnlyList<SharedPool> SharedPools,
    IReadOnlyList<ReportPool> LocalSystemPools,
    IReadOnlyList<ReportCert> ExpiringCertificates);

public static class DiscoveryReportBuilder
{
    public static DiscoveryReport Build(
        IEnumerable<IisSite> sites,
        IEnumerable<IisAppPool> pools,
        IEnumerable<IisCertificate> certs,
        DateTimeOffset now,
        int certWarningDays = 30)
    {
        var siteList = sites.ToList();

        var unassigned = siteList
            .Where(s => s.MappingStatus == MappingStatus.Unassigned)
            .Select(s => new ReportSite(s.ServerId, s.Name, s.PhysicalPath, s.AppPoolName))
            .OrderBy(s => s.Name).ToList();

        // A pool is "shared" when more than one application (site root or sub-application) uses it on the same server.
        var shared = new List<SharedPool>();
        foreach (var byServer in siteList.GroupBy(s => s.ServerId))
        {
            var uses = byServer
                .SelectMany(s => s.Applications.Select(a => (Pool: a.AppPoolName, Label: $"{s.Name}{(a.Path == "/" ? "" : a.Path)}")))
                .Where(u => !string.IsNullOrEmpty(u.Pool))
                .GroupBy(u => u.Pool!, StringComparer.OrdinalIgnoreCase);
            foreach (var g in uses)
            {
                var labels = g.Select(x => x.Label).Distinct().OrderBy(x => x).ToList();
                if (labels.Count > 1) shared.Add(new SharedPool(byServer.Key, g.Key, labels));
            }
        }

        var system = pools
            .Where(p => string.Equals(p.IdentityType, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            .Select(p => new ReportPool(p.ServerId, p.Name, p.IdentityType))
            .OrderBy(p => p.Name).ToList();

        var expiring = certs
            .Select(c => new ReportCert(c.ServerId, c.Thumbprint, c.Subject, c.NotAfter, (int)Math.Floor((c.NotAfter - now).TotalDays)))
            .Where(c => c.DaysLeft <= certWarningDays)
            .OrderBy(c => c.DaysLeft).ToList();

        return new DiscoveryReport(unassigned, shared, system, expiring);
    }
}
