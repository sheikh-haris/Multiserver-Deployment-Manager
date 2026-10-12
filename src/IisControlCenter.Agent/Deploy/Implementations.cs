using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using IisControlCenter.Agent.Config;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using Microsoft.Extensions.Logging;

namespace IisControlCenter.Agent.Deploy;

/// <summary>Downloads the package from the portal over HTTPS with the agent's client certificate. Resumes after a dropped connection.</summary>
public sealed class HttpPackageSource(AgentConfig config, Func<X509Certificate2> certificate, ILogger<HttpPackageSource> log) : IPackageSource
{
    public async Task DownloadAsync(Guid runId, Guid packageId, string destinationFile, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate());
#if DEBUG
        if (Environment.GetEnvironmentVariable("ICC_INSECURE_DEV_TLS") == "1") handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var url = $"{config.PortalUrl.TrimEnd('/')}{ContractInfo.Routes.AgentPackages}/{packageId}?run={runId}";

        long have = File.Exists(destinationFile) ? new FileInfo(destinationFile).Length : 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return;   // already complete
                resp.EnsureSuccessStatusCode();
                var append = resp.StatusCode == HttpStatusCode.PartialContent;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(destinationFile, append ? FileMode.Append : FileMode.Create, FileAccess.Write);
                await src.CopyToAsync(dst, ct);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 5 && !ct.IsCancellationRequested)
            {
                have = File.Exists(destinationFile) ? new FileInfo(destinationFile).Length : 0;
                log.LogWarning("Download interrupted ({Message}). Resuming from byte {Have}.", ex.Message, have);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }
}

/// <summary>Calls the site on THIS server only (127.0.0.1, or the binding's own address) with the right Host header.</summary>
public sealed class LocalHealthChecker : IHealthChecker
{
    public async Task<HealthResult> CheckAsync(IReadOnlyList<DiscoveredBinding> bindings, string path, int expectedStatus, TimeSpan timeout, CancellationToken ct)
    {
        var usable = bindings.Where(b => b.Port > 0 && b.Protocol is "http" or "https").OrderBy(b => b.Protocol == "http" ? 0 : 1).ToList();
        if (usable.Count == 0) return new(false, "The site has no http or https binding to call.");
        var b = usable[0];
        var address = string.IsNullOrEmpty(b.IpAddress) || b.IpAddress is "*" or "0.0.0.0" or "::" ? "127.0.0.1" : b.IpAddress;
        var url = $"{b.Protocol}://{address}:{b.Port}{(path.StartsWith('/') ? path : "/" + path)}";

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (b.Protocol == "https") handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;   // loopback call: the name will not match the certificate
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var deadline = DateTime.UtcNow + timeout;
        string last = "no response";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(b.Host)) req.Headers.Host = b.Host;
                using var resp = await http.SendAsync(req, ct);
                var code = (int)resp.StatusCode;
                last = $"GET {url} -> {code}";
                if (expectedStatus == 0 ? code is >= 200 and < 300 : code == expectedStatus) return new(true, last);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { last = $"GET {url} -> {ex.GetType().Name}: {ex.Message}"; }
            if (DateTime.UtcNow >= deadline) return new(false, last + $" (gave up after {timeout.TotalSeconds:0}s)");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

public sealed class DefenderAgentAntivirus : IAgentAntivirus
{
    public Task<ScanResult> ScanAsync(string file, CancellationToken ct)
    {
#if DEBUG
        // Development machines without Defender (the Linux browser smoke test). Not compiled into Release builds.
        if (Environment.GetEnvironmentVariable("ICC_DEV_SKIP_SCAN") == "1") return Task.FromResult(new ScanResult(ScanOutcome.Clean, "scan skipped (development)"));
#endif
        return DefenderScanner.ScanFileAsync(file, TimeSpan.FromMinutes(15), ct);
    }
}

/// <summary>Fetches the plan input of a run and the path list of a manifest request from the portal over HTTPS.</summary>
public sealed class HttpPlanSource(AgentConfig config, Func<X509Certificate2> certificate) : IRunPlanSource, IManifestInputSource
{
    private HttpClient Client()
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate());
#if DEBUG
        if (Environment.GetEnvironmentVariable("ICC_INSECURE_DEV_TLS") == "1") handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<RunPlanInput> GetAsync(Guid runId, CancellationToken ct)
    {
        using var http = Client();
        return await http.GetFromJsonAsync<RunPlanInput>($"{config.PortalUrl.TrimEnd('/')}{ContractInfo.Routes.AgentRuns}/{runId}/plan", ct)
               ?? throw new InvalidOperationException("The portal returned no plan.");
    }

    public async Task<IReadOnlyList<string>> GetPathsAsync(Guid commandId, CancellationToken ct)
    {
        using var http = Client();
        return await http.GetFromJsonAsync<List<string>>($"{config.PortalUrl.TrimEnd('/')}{ContractInfo.Routes.AgentResults}/manifest-input/{commandId}", ct) ?? [];
    }
}
