using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Web.Administration;

namespace IisControlCenter.Agent.IntegrationTests;

/// <summary>
/// Builds a small, uniquely named IIS configuration on the machine running the tests, and removes it afterwards.
/// These tests need Windows with the IIS role and administrator rights (a GitHub-hosted Windows runner works).
/// Everything here is prefixed "icc-it-" so it cannot touch real sites.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IisFixture : IDisposable
{
    public const string Secret = "S3cr3t!Pw-should-never-leave-the-server";
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string SiteName => $"icc-it-site-{Id}";
    public string PoolName => $"icc-it-pool-{Id}";
    public string CustomPoolName => $"icc-it-custom-{Id}";
    public string Root { get; }
    public string Thumbprint { get; private set; } = "";
    public bool Available { get; }
    public string SkipReason { get; } = "";

    public IisFixture()
    {
        if (!OperatingSystem.IsWindows()) { SkipReason = "Not Windows."; Root = ""; return; }
        if (!IsAdmin()) { SkipReason = "Needs administrator rights."; Root = ""; return; }
        if (!new IisControlCenter.Agent.Iis.IisInspector().IsIisInstalled()) { SkipReason = "IIS is not installed."; Root = ""; return; }

        Root = Path.Combine(Path.GetTempPath(), "icc-it-" + Id);
        Directory.CreateDirectory(Path.Combine(Root, "web", "api"));
        Directory.CreateDirectory(Path.Combine(Root, "uploads"));

        using var mgr = new ServerManager();
        var pool = mgr.ApplicationPools.Add(PoolName);
        pool.ManagedRuntimeVersion = "v4.0";
        pool.ManagedPipelineMode = ManagedPipelineMode.Integrated;

        // A pool with a custom identity and a password: discovery must NEVER return the password.
        var custom = mgr.ApplicationPools.Add(CustomPoolName);
        custom.ProcessModel.IdentityType = ProcessModelIdentityType.SpecificUser;
        custom.ProcessModel.UserName = @".\icc-it-nobody";
        custom.ProcessModel.Password = Secret;
        custom.ManagedRuntimeVersion = "";

        Thumbprint = AddTestCertificate();
        var site = mgr.Sites.Add(SiteName, "http", $"*:{FreePort()}:{Id}.icc-it.local", Path.Combine(Root, "web"));
        site.ApplicationDefaults.ApplicationPoolName = PoolName;
        site.Applications["/"].ApplicationPoolName = PoolName;
        var api = site.Applications.Add("/api", Path.Combine(Root, "web", "api"));
        api.ApplicationPoolName = CustomPoolName;
        site.Applications["/"].VirtualDirectories.Add("/uploads", Path.Combine(Root, "uploads"));
        var https = site.Bindings.Add($"*:{FreePort()}:{Id}.icc-it.local", Convert.FromHexString(Thumbprint), "My");
        mgr.CommitChanges();
        Available = true;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private string AddTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Id}.icc-it.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        var pfx = self.Export(X509ContentType.Pfx, "x");
        var cert = X509CertificateLoader.LoadPkcs12(pfx, "x", X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        return cert.Thumbprint;
    }

    private static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        if (!Available) return;
        try
        {
            using var mgr = new ServerManager();
            mgr.Sites[SiteName]?.Delete();
            mgr.ApplicationPools[PoolName]?.Delete();
            mgr.ApplicationPools[CustomPoolName]?.Delete();
            mgr.CommitChanges();
        }
        catch { /* best effort */ }
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, Thumbprint, false)) store.Remove(c);
        }
        catch { }
        try { Directory.Delete(Root, true); } catch { }
    }
}
