using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IisControlCenter.Agent.Security;

/// <summary>Creates and loads the agent's own client certificate. The private key never leaves this server.</summary>
public static class AgentCertificates
{
    private static StoreLocation Location => OperatingSystem.IsWindows() ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;

    public static X509Certificate2 CreateAndInstall(string hostname)
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest($"CN=icc-agent-{hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));   // client auth

        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));

        // Round trip through PKCS#12 so the key is persisted in the machine store and is NOT exportable afterwards.
        var pwd = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = selfSigned.Export(X509ContentType.Pfx, pwd);
        var flags = X509KeyStorageFlags.PersistKeySet | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);
        var persisted = X509CertificateLoader.LoadPkcs12(pfx, pwd, flags);

        using var store = new X509Store(StoreName.My, Location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    public static X509Certificate2 Load(string sha256Thumbprint)
    {
        using var store = new X509Store(StoreName.My, Location);
        store.Open(OpenFlags.ReadOnly);
        foreach (var c in store.Certificates)
            if (string.Equals(Convert.ToHexString(SHA256.HashData(c.RawData)), sha256Thumbprint, StringComparison.OrdinalIgnoreCase) && c.HasPrivateKey)
                return c;
        throw new InvalidOperationException("Agent certificate not found in the certificate store. Re-register the agent.");
    }

    public static void Remove(string sha256Thumbprint)
    {
        using var store = new X509Store(StoreName.My, Location);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.ToArray())
            if (string.Equals(Convert.ToHexString(SHA256.HashData(c.RawData)), sha256Thumbprint, StringComparison.OrdinalIgnoreCase))
                store.Remove(c);
    }

    public static string Sha256Thumbprint(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData));
}
