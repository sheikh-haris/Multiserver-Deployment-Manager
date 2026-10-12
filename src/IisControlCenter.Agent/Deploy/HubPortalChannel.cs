using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IisControlCenter.Agent.Config;
using IisControlCenter.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace IisControlCenter.Agent.Deploy;

/// <summary>Talks to the portal over the agent's SignalR connection (small messages) and HTTPS (big uploads).</summary>
public sealed class HubPortalChannel(AgentConfig config, Func<HubConnection?> hub, Func<X509Certificate2> certificate) : IPortalChannel
{
    private HubConnection Connected() =>
        hub() is { State: HubConnectionState.Connected } h ? h : throw new InvalidOperationException("Not connected to the portal.");

    public async Task SendStepAsync(RunStep step, CancellationToken ct) { if (hub() is { State: HubConnectionState.Connected } h) await h.SendAsync("ReportRunStep", step, ct); }
    public async Task SendLogAsync(RunLog log, CancellationToken ct) { if (hub() is { State: HubConnectionState.Connected } h) await h.SendAsync("ReportRunLog", log, ct); }
    public Task<bool> SendCompletedAsync(RunCompleted result, CancellationToken ct) => Connected().InvokeAsync<bool>("ReportRunCompleted", result, ct);
    public Task SendMaskedFileAsync(MaskedFileResult result, CancellationToken ct) => Connected().InvokeAsync("ReportMaskedFile", result, ct);
    public Task SendRejectedAsync(CommandRejected rejected, CancellationToken ct) => Connected().InvokeAsync("ReportRejected", rejected, ct);

    public async Task UploadManifestAsync(ManifestResult result, CancellationToken ct)
    {
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate());
#if DEBUG
        if (Environment.GetEnvironmentVariable("ICC_INSECURE_DEV_TLS") == "1") handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#endif
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        await using var ms = new MemoryStream();
        await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(gz, result, cancellationToken: ct);
        ms.Position = 0;
        using var content = new StreamContent(ms);
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        using var resp = await http.PutAsync($"{config.PortalUrl.TrimEnd('/')}{ContractInfo.Routes.AgentResults}/manifest/{result.CommandId}", content, ct);
        resp.EnsureSuccessStatusCode();
    }
}
