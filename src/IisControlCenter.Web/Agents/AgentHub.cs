using IisControlCenter.Contracts;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IisControlCenter.Web.Agents;

/// <summary>Agents connect here (mutual TLS). The portal never connects to agents.</summary>
[Authorize(AuthenticationSchemes = AgentCertAuth.Scheme)]
public sealed class AgentHub(
    ServerService servers,
    AgentConnectionTracker tracker,
    AgentCommandService commands,
    AuditService audit,
    RunReportService runs,
    PendingAgentResults pending,
    ILogger<AgentHub> log) : Hub<IAgentClient>
{
    private Guid ServerId => Guid.Parse(Context.User!.FindFirst(AgentCertAuth.ServerIdClaim)!.Value);
    private string Thumbprint => Context.User!.FindFirst(AgentCertAuth.ThumbprintClaim)!.Value;

    public override async Task OnConnectedAsync()
    {
        tracker.Add(ServerId, Context.ConnectionId);
        await servers.SupersedeOlderCertificatesAsync(ServerId, Thumbprint);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (tracker.Remove(ServerId, Context.ConnectionId))
            await servers.OnDisconnectedAsync(ServerId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>First call after connecting. Returns false when the portal rejects the agent (it should stop).</summary>
    public async Task<bool> Hello(AgentHello hello)
    {
        await EnsureCertificateActiveAsync();
        var (outcome, reason) = await servers.OnHelloAsync(ServerId, hello);
        if (outcome == ServerService.HelloOutcome.Rejected)
        {
            log.LogWarning("Agent {Server} rejected: {Reason}", ServerId, reason);
            Context.Abort();
            return false;
        }
        return true;
    }

    public async Task SendHeartbeat(Heartbeat hb)
    {
        await EnsureCertificateActiveAsync();
        await servers.OnHeartbeatAsync(ServerId, hb);
    }

    public async Task<bool> ReportDiscovery(DiscoveryResult result)
    {
        await EnsureCertificateActiveAsync();
        // Ignore results we did not ask for.
        if (!commands.ConsumeIssued(result.CommandId, ServerId))
        {
            await audit.WriteSystemAsync(new AuditRecord("Discovery.Received", "Server", ServerId.ToString(), ServerId,
                Result: "Denied", Detail: "Unsolicited discovery result ignored."));
            return false;
        }
        var r = await servers.StoreDiscoveryAsync(ServerId, result);
        return r.Ok;
    }

    /// <summary>Returns the new certificate's thumbprint, or null when the portal refuses.</summary>
    public async Task<string?> RenewCertificate(string newCertificateBase64)
    {
        await EnsureCertificateActiveAsync();
        var r = await servers.RenewCertificateAsync(ServerId, Thumbprint, newCertificateBase64);
        if (!r.Ok) log.LogWarning("Certificate renewal for {Server} refused: {Error}", ServerId, r.Error);
        return r.Ok ? r.Value : null;
    }

    public Task ReportRejected(CommandRejected rejected) =>
        audit.WriteSystemAsync(new AuditRecord("Agent.CommandRejected", "Server", ServerId.ToString(), ServerId,
            Result: "Rejected", Detail: $"{rejected.CommandId}: {rejected.Reason}"));

    // ---- deployments ----

    public async Task ReportRunStep(RunStep step) { await EnsureCertificateActiveAsync(); await runs.StepAsync(ServerId, step); }

    public async Task ReportRunLog(RunLog logLines) { await EnsureCertificateActiveAsync(); await runs.LogAsync(ServerId, logLines); }

    /// <summary>True = stored; the agent may forget the result.</summary>
    public async Task<bool> ReportRunCompleted(RunCompleted result) { await EnsureCertificateActiveAsync(); return await runs.CompletedAsync(ServerId, result); }

    public async Task ReportMaskedFile(MaskedFileResult result) { await EnsureCertificateActiveAsync(); pending.CompleteMasked(ServerId, result); }

    public async Task ReportIisResult(IisOpResult result) { await EnsureCertificateActiveAsync(); pending.CompleteIis(ServerId, result); }

    public async Task ReportPoolResult(PoolOpResult result) { await EnsureCertificateActiveAsync(); pending.CompletePool(ServerId, result); }

    private async Task EnsureCertificateActiveAsync()
    {
        if (!await servers.IsCertificateActiveAsync(ServerId, Thumbprint))
        {
            Context.Abort();
            throw new HubException("Certificate is no longer valid.");
        }
    }
}
