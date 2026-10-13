using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace IisControlCenter.Agent;

/// <summary>
/// Dials OUT to the portal over mutual TLS and executes only the allow-listed commands from IAgentClient.
/// Phase 1 commands: RunDiscovery, Ping, Disconnect. Nothing else exists in this program.
/// </summary>
public sealed class AgentWorker(
    AgentConfig config,
    CommandPolicy policy,
    IIisInspector iis,
    TimeProvider clock,
    IServiceProvider services,
    ILogger<AgentWorker> log) : BackgroundService
{
    private IisControlCenter.Agent.Deploy.RunCoordinator? _runs;
    private IisControlCenter.Agent.Pools.PoolHandler? _pools;
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfterRejection = TimeSpan.FromMinutes(5);
    private HubConnection? _hub;
    private volatile bool _stop;
    private System.Security.Cryptography.X509Certificates.X509Certificate2 _cert = default!;
    private static readonly TimeSpan RenewWhenLeft = RenewalPolicy.DefaultWindow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Agent {Version} starting. Environment {Env}. Allowed roots: {Roots}",
            AgentInfo.Version, config.Environment, string.Join(", ", config.AllowedRoots));

        _cert = AgentCertificates.Load(config.CertThumbprint);
        _hub = BuildConnection(_cert);
        Wire(_hub);

        var channel = new IisControlCenter.Agent.Deploy.HubPortalChannel(config, () => _hub, () => _cert);
        _runs = ActivatorUtilities.CreateInstance<IisControlCenter.Agent.Deploy.RunCoordinator>(services, (IisControlCenter.Agent.Deploy.IPortalChannel)channel);
        _pools = services.GetRequiredService<IisControlCenter.Agent.Pools.PoolHandler>();
        try { _runs.RecoverAfterRestart(ct); }       // undo any deployment that was cut off by a crash or reboot
        catch (Exception ex) { log.LogError(ex, "Recovery after restart failed"); }

        var attempt = 0;
        while (!ct.IsCancellationRequested && !_stop)
        {
            try
            {
                if (_hub!.State == HubConnectionState.Disconnected)
                {
                    await _hub.StartAsync(ct);
                    attempt = 0;
                    if (!await HelloAsync(ct))
                    {
                        log.LogWarning("Portal rejected this agent. Retrying in {Min} minutes.", RetryAfterRejection.TotalMinutes);
                        await _hub.StopAsync(CancellationToken.None);
                        await Task.Delay(RetryAfterRejection, ct);
                        continue;
                    }
                    log.LogInformation("Connected to portal.");
                    if (await TryRenewCertificateAsync(ct)) continue;   // reconnect right away with the new certificate
                    _ = _runs!.ResendUnconfirmedAsync(ct);               // results the portal has not confirmed yet
                }
                else if (_hub.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync("SendHeartbeat", new Heartbeat(clock.GetUtcNow(), iis.IsIisInstalled()), ct);
                }
                await Task.Delay(HeartbeatEvery, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                attempt++;
                var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
                log.LogWarning("Connection problem: {Message}. Retrying in {Seconds}s.", ex.Message, (int)wait.TotalSeconds);
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }

        if (_hub is not null) await _hub.DisposeAsync();
    }

    /// <summary>
    /// Replaces the client certificate before it expires. A new key pair is made on this server, the portal pins it,
    /// and the agent reconnects with it. The old certificate keeps working until the new one has been used once.
    /// Returns true when the agent switched certificates and must reconnect.
    /// </summary>
    private async Task<bool> TryRenewCertificateAsync(CancellationToken ct)
    {
        var left = _cert.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime;
        var window = RenewWhenLeft;
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("ICC_DEV_RENEW_DAYS"), out var devDays)) window = TimeSpan.FromDays(devDays);   // lets a developer see renewal happen
#endif
        if (!RenewalPolicy.ShouldRenew(_cert.NotBefore.ToUniversalTime(), _cert.NotAfter.ToUniversalTime(), clock.GetUtcNow().UtcDateTime, window)) return false;
        try
        {
            log.LogInformation("Agent certificate expires in {Days} days. Renewing.", (int)left.TotalDays);
            var fresh = AgentCertificates.CreateAndInstall(Environment.MachineName);
            var thumb = AgentCertificates.Sha256Thumbprint(fresh);
            var accepted = await _hub!.InvokeAsync<string?>("RenewCertificate", Convert.ToBase64String(fresh.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert)), ct);
            if (!string.Equals(accepted, thumb, StringComparison.OrdinalIgnoreCase))
            {
                AgentCertificates.Remove(thumb);
                log.LogWarning("The portal refused the certificate renewal. Will try again on the next connection.");
                return false;
            }
            var oldThumb = config.CertThumbprint;
            config.CertThumbprint = thumb;
            config.Save(config.SourcePath);
            await _hub!.StopAsync(CancellationToken.None);
            await _hub!.DisposeAsync();
            _cert = fresh;
            _hub = BuildConnection(_cert);
            Wire(_hub);
            log.LogInformation("Switched to the new certificate. Old one ({Old}…) stays in the store until the portal retires it.", oldThumb[..Math.Min(8, oldThumb.Length)]);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Certificate renewal failed: {Message}. Will try again on the next connection.", ex.Message);
            return false;
        }
    }

    private HubConnection BuildConnection(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        var url = config.PortalUrl.TrimEnd('/') + ContractInfo.Routes.AgentHub;
        return new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { cert };
#if DEBUG
                if (Environment.GetEnvironmentVariable("ICC_INSECURE_DEV_TLS") == "1")
                {
                    o.HttpMessageHandlerFactory = h =>
                    {
                        if (h is HttpClientHandler hc) hc.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                        return h;
                    };
                    o.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
#endif
            })
            .WithAutomaticReconnect(new RetryForever())
            .Build();
    }

    private void Wire(HubConnection hub)
    {
        hub.On<CommandEnvelope>(nameof(IAgentClient.RunDiscovery), e => _ = HandleDiscoveryAsync(e));
        hub.On<CommandEnvelope>(nameof(IAgentClient.Ping), e => HandlePing(e));
        hub.On<CommandEnvelope, ManifestRequest>(nameof(IAgentClient.GetManifest), (e, r) => _ = Guard(() => _runs!.HandleManifestAsync(e, r, _stopping.Token), "manifest"));
        hub.On<CommandEnvelope, MaskedFileRequest>(nameof(IAgentClient.GetMaskedFile), (e, r) => _ = Guard(() => _runs!.HandleMaskedFileAsync(e, r, _stopping.Token), "masked file"));
        hub.On<CommandEnvelope, DeployCommand>(nameof(IAgentClient.Deploy), (e, c) => _ = Guard(() => _runs!.HandleDeployAsync(e, c, _stopping.Token), "deploy"));
        hub.On<CommandEnvelope, RollbackCommand>(nameof(IAgentClient.RollbackRun), (e, c) => _ = Guard(() => _runs!.HandleRollbackAsync(e, c, _stopping.Token), "rollback"));
        hub.On<CommandEnvelope, PoolActionCommand>(nameof(IAgentClient.PoolAction), (e, c) => _ = Guard(async () => await ReportPoolAsync(await _pools!.ActAsync(e, c)), "pool action"));
        hub.On<CommandEnvelope, UpdatePoolCommand>(nameof(IAgentClient.UpdatePool), (e, c) => _ = Guard(async () => await ReportPoolAsync(await _pools!.UpdateAsync(e, c)), "pool update"));
        hub.On<string>(nameof(IAgentClient.Disconnect), reason =>
        {
            log.LogWarning("Portal asked this agent to disconnect: {Reason}. Not reconnecting until the service restarts.", reason);
            _stop = true;
            _ = hub.StopAsync();
        });
        hub.Reconnected += async _ =>
        {
            try { if (!await HelloAsync(CancellationToken.None)) await hub.StopAsync(); }
            catch (Exception ex) { log.LogWarning(ex, "Hello after reconnect failed"); }
        };
        hub.Closed += ex => { log.LogInformation("Disconnected: {Message}", ex?.Message ?? "closed"); return Task.CompletedTask; };
    }

    private readonly CancellationTokenSource _stopping = new();

    private async Task Guard(Func<Task> work, string what)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogError(ex, "Handling {What} failed", what); }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();          // an interrupted deployment is restored by the journal when the agent starts again
        return base.StopAsync(cancellationToken);
    }

    private async Task<bool> HelloAsync(CancellationToken ct) =>
        await _hub!.InvokeAsync<bool>("Hello", new AgentHello(
            ContractInfo.Version, AgentInfo.Version, Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            config.Environment, config.AllowManaged, config.AllowedRoots, clock.GetUtcNow()), ct);

    private void HandlePing(CommandEnvelope e)
    {
        var refusal = policy.Validate(e);
        if (refusal is not null) _ = RejectAsync(e, refusal);
    }

    private async Task HandleDiscoveryAsync(CommandEnvelope e)
    {
        var refusal = policy.Validate(e);
        if (refusal is not null) { await RejectAsync(e, refusal); return; }
        try
        {
            log.LogInformation("Running discovery (command {Id})", e.CommandId);
            var result = await Task.Run(() => iis.Collect(e.CommandId));
            var accepted = await _hub!.InvokeAsync<bool>("ReportDiscovery", result);
            log.LogInformation("Discovery sent: {Sites} sites, {Pools} pools. Accepted by portal: {Accepted}", result.Sites.Count, result.AppPools.Count, accepted);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Discovery failed");
            await RejectAsync(e, "Discovery failed on the server: " + ex.GetType().Name);
        }
    }

    private async Task ReportPoolAsync(PoolOpResult result)
    {
        if (_hub is { State: HubConnectionState.Connected } h) await h.InvokeAsync("ReportPoolResult", result);
    }

    private async Task RejectAsync(CommandEnvelope e, string reason)
    {
        log.LogWarning("Refused command {Id}: {Reason}", e.CommandId, reason);
        try { await _hub!.InvokeAsync("ReportRejected", new CommandRejected(e.CommandId, reason)); }
        catch (Exception ex) { log.LogWarning(ex, "Could not report the refusal"); }
    }

    private sealed class RetryForever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext c) => TimeSpan.FromSeconds(Math.Min(60, 2 + c.PreviousRetryCount * 5));
    }
}
