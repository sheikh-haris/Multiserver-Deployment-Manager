using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Iis;

/// <summary>
/// Fake data for trying the portal on a non-Windows development machine.
/// The agent refuses to use it on Windows, so it can never hide a real server.
/// </summary>
public sealed class SampleInspector : IIisInspector
{
    public bool IsIisInstalled() => true;

    public DiscoveryResult Collect(Guid commandId)
    {
        var now = DateTimeOffset.UtcNow;
        if (SampleSites.Load() is { } configured)
            return new DiscoveryResult(commandId, now, true,
                configured.Select(c => new DiscoveredSite(c.Id, c.Name, "Started", c.Path, c.Pool,
                    [new DiscoveredBinding("http", "*", c.Port, c.Host, null)],
                    [new DiscoveredApplication("/", c.Path, c.Pool, [new DiscoveredVirtualDirectory("/", c.Path)])])).ToList(),
                configured.Select(c => c.Pool).Distinct().Select(p => new DiscoveredAppPool(p, "ApplicationPoolIdentity", "v4.0", "Integrated", "Started")).ToList(),
                [], [new DiscoveredDotNetVersion(".NET Framework", "4.8")]);
        DiscoveredSite Site(long id, string name, string path, string pool, string host) => new(id, name, "Started", path, pool,
            [new DiscoveredBinding("http", "*", 80, host, null), new DiscoveredBinding("https", "*", 443, host, "AABBCCDDEEFF00112233445566778899AABBCCDD")],
            [new DiscoveredApplication("/", path, pool, [new DiscoveredVirtualDirectory("/", path)]),
             new DiscoveredApplication("/api", path + @"\api", pool, [new DiscoveredVirtualDirectory("/", path + @"\api"), new DiscoveredVirtualDirectory("/uploads", @"D:\Data\uploads")])]);
        // ICC_SAMPLE_VARIANT=2 imitates someone changing IIS Manager by hand, so drift detection can be tried locally.
        var changed = Environment.GetEnvironmentVariable("ICC_SAMPLE_VARIANT") == "2";
        var sites = changed
            ? new[] { Site(1, "Default Web Site", @"C:\inetpub\wwwroot", "DefaultAppPool", "shop.example.local"),
                      Site(3, "payments-web-v2", @"D:\Websites\payments\web", "PaymentsPool", "pay.example.local"),
                      Site(4, "brand-new", @"D:\Websites\brand-new", "NewPool", "new.example.local") }
            : new[] { Site(1, "Default Web Site", @"C:\inetpub\wwwroot", "DefaultAppPool", "shop.example.local"),
                      Site(2, "legacy-portal", @"D:\Websites\legacy", "DefaultAppPool", "legacy.example.local"),
                      Site(3, "payments-web", @"D:\Websites\payments\web", "PaymentsPool", "pay.example.local") };
        return new DiscoveryResult(commandId, now, true, sites,
            [new DiscoveredAppPool("DefaultAppPool", "ApplicationPoolIdentity", "v4.0", "Integrated", "Started"),
             new DiscoveredAppPool("PaymentsPool", "LocalSystem", "", "Integrated", "Started"),
             ..(changed ? new[] { new DiscoveredAppPool("NewPool", "ApplicationPoolIdentity", "v4.0", "Integrated", "Started") } : [])],
            [new DiscoveredCertificate("AABBCCDDEEFF00112233445566778899AABBCCDD", "CN=shop.example.local", now.AddDays(12), "My"),
             new DiscoveredCertificate("11223344556677889900AABBCCDDEEFF00112233", "CN=pay.example.local", now.AddDays(200), "WebHosting")],
            [new DiscoveredDotNetVersion(".NET Framework", "4.8"), new DiscoveredDotNetVersion("ASP.NET Core runtime", "10.0.0")]);
    }
}
