using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Registration;

/// <summary>
/// Run once by the installer: IisControlCenter.Agent.exe register --portal URL --environment Dev --roots "D:\Websites;E:\Sites" [--allow-managed]
/// The registration token is read from the ICC_TOKEN environment variable so it does not sit in a command line.
/// </summary>
public static class RegisterCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        var token = System.Environment.GetEnvironmentVariable("ICC_TOKEN");
        var cfg = new AgentConfig
        {
            PortalUrl = (Arg("--portal") ?? "").TrimEnd('/'),
            Environment = Arg("--environment") ?? "",
            AllowedRoots = (Arg("--roots") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            AllowManaged = args.Contains("--allow-managed"),
        };
        var configPath = Arg("--config") ?? AgentConfig.DefaultPath;

        // Normalise environment casing to the known name.
        cfg.Environment = AgentConfig.KnownEnvironments.FirstOrDefault(e => e.Equals(cfg.Environment, StringComparison.OrdinalIgnoreCase)) ?? cfg.Environment;
        cfg.AllowedRoots = cfg.AllowedRoots.Select(r => PathGuard.Normalize(r) ?? r).ToList();

        var problems = cfg.Validate();
        if (string.IsNullOrWhiteSpace(token)) problems.Add("ICC_TOKEN environment variable is not set");
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("Cannot register:\n - " + string.Join("\n - ", problems));
            return 2;
        }

        var host = System.Environment.MachineName;
        using var cert = AgentCertificates.CreateAndInstall(host);
        var thumb = AgentCertificates.Sha256Thumbprint(cert);

        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(cert);
#if DEBUG
        if (System.Environment.GetEnvironmentVariable("ICC_INSECURE_DEV_TLS") == "1")
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var req = new RegisterRequest(token!, host, cfg.Environment, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            AgentInfo.Version, IsDomainJoined(), Convert.ToBase64String(cert.Export(X509ContentType.Cert)));

        HttpResponseMessage resp;
        try { resp = await http.PostAsJsonAsync(cfg.PortalUrl + ContractInfo.Routes.Register, req); }
        catch (Exception ex)
        {
            AgentCertificates.Remove(thumb);
            Console.Error.WriteLine($"Could not reach the portal: {ex.Message}");
            return 3;
        }

        if (!resp.IsSuccessStatusCode)
        {
            AgentCertificates.Remove(thumb);       // do not leave an unused key pair behind
            Console.Error.WriteLine($"Portal refused registration ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            return 4;
        }
        var result = (await resp.Content.ReadFromJsonAsync<RegisterResponse>())!;
        cfg.ServerId = result.ServerId;
        cfg.CertThumbprint = thumb;
        cfg.Save(configPath);
        Console.WriteLine($"Registered. Server id {result.ServerId}. The server is Pending until an admin approves it in the portal.");
        return 0;
    }

    private static bool IsDomainJoined()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var rc = NetGetJoinInformation(null, out var name, out var status);
            if (name != IntPtr.Zero) NetApiBufferFree(name);
            return rc == 0 && status == 3;   // NetSetupDomainName
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("netapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [System.Runtime.InteropServices.DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
