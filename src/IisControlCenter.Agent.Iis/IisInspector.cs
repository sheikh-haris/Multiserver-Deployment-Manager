using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using IisControlCenter.Contracts;
using Microsoft.Win32;
using Microsoft.Web.Administration;

namespace IisControlCenter.Agent.Iis;

/// <summary>
/// Reads IIS with Microsoft.Web.Administration. Deliberately never touches
/// ProcessModel.Password, connection strings, or any file under the site folders.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IisInspector : IIisInspector
{
    public bool IsIisInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
        return key?.GetValue("MajorVersion") is not null;
    }

    public DiscoveryResult Collect(Guid commandId)
    {
        var now = DateTimeOffset.UtcNow;
        var iis = IsIisInstalled();
        var sites = new List<DiscoveredSite>();
        var pools = new List<DiscoveredAppPool>();

        if (iis)
        {
            using var mgr = new ServerManager();
            foreach (var site in mgr.Sites)
            {
                var apps = site.Applications.Select(a => new DiscoveredApplication(
                    a.Path,
                    Expand(a.VirtualDirectories.FirstOrDefault(v => v.Path == "/")?.PhysicalPath),
                    a.ApplicationPoolName,
                    a.VirtualDirectories.Select(v => new DiscoveredVirtualDirectory(v.Path, Expand(v.PhysicalPath))).ToList())).ToList();
                var root = apps.FirstOrDefault(a => a.Path == "/");

                sites.Add(new DiscoveredSite(
                    site.Id, site.Name, SafeState(() => site.State.ToString()),
                    root?.PhysicalPath, root?.AppPoolName,
                    site.Bindings.Select(b => new DiscoveredBinding(
                        b.Protocol,
                        b.EndPoint?.Address?.ToString(),
                        b.EndPoint?.Port ?? 0,
                        string.IsNullOrEmpty(b.Host) ? null : b.Host,
                        b.CertificateHash is { Length: > 0 } h ? Convert.ToHexString(h) : null)).ToList(),
                    apps));
            }

            foreach (var p in mgr.ApplicationPools)
            {
                pools.Add(new DiscoveredAppPool(
                    p.Name,
                    p.ProcessModel.IdentityType.ToString(),    // type only; password is never read
                    string.IsNullOrEmpty(p.ManagedRuntimeVersion) ? null : p.ManagedRuntimeVersion,
                    p.ManagedPipelineMode.ToString(),
                    SafeState(() => p.State.ToString())));
            }
        }

        return new DiscoveryResult(commandId, now, iis, sites, pools, ReadCertificates(), DotNetVersions.Read());
    }

    private static string Expand(string? p) => string.IsNullOrEmpty(p) ? "" : Environment.ExpandEnvironmentVariables(p);

    private static string SafeState(Func<string> f)
    {
        try { return f(); } catch { return "Unknown"; }
    }

    private static List<DiscoveredCertificate> ReadCertificates()
    {
        var list = new List<DiscoveredCertificate>();
        foreach (var name in new[] { StoreName.My.ToString(), "WebHosting" })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates)     // metadata only; private keys are not accessed
                {
                    list.Add(new DiscoveredCertificate(c.Thumbprint, c.Subject, c.NotAfter.ToUniversalTime(), name));
                    c.Dispose();
                }
            }
            catch { /* store missing: skip */ }
        }
        return list;
    }
}
