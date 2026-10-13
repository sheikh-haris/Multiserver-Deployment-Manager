using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IisControlCenter.Web.Agents;

/// <summary>Sends allow-listed commands to agents. Remembers issued command ids so unsolicited results are ignored.</summary>
public sealed class AgentCommandService(
    IDbContextFactory<AppDbContext> dbf,
    IHubContext<AgentHub, IAgentClient> hub,
    AgentConnectionTracker tracker,
    IMemoryCache cache,
    AuditService audit,
    TimeProvider clock)
{
    private static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(5);

    public async Task<OpResult> RequestDiscoveryAsync(Actor actor, Guid serverId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult.Fail("Server not found.");
        if (!actor.Can(Permission.TriggerDiscovery, s.EnvironmentName))
        {
            await audit.WriteAsync(actor, new AuditRecord("Discovery.Request", "Server", s.Id.ToString(), s.Id, s.EnvironmentName, Result: "Denied"));
            throw new ForbiddenException("You do not have permission to run discovery on this server.");
        }
        var r = await SendDiscoveryAsync(s);
        await audit.WriteAsync(actor, new AuditRecord("Discovery.Request", "Server", s.Id.ToString(), s.Id, s.EnvironmentName,
            Result: r.Ok ? "Success" : "Failed", Detail: r.Error));
        return r;
    }

    /// <summary>Used by the scheduler and by approval. No user involved.</summary>
    public async Task<OpResult> SendDiscoveryAsync(Server s)
    {
        if (s.EnvironmentName is null) return OpResult.Fail("Server is not approved yet.");
        if (s.AgentStatus == AgentStatus.Revoked) return OpResult.Fail("Agent is revoked.");
        var connection = tracker.GetConnectionId(s.Id);
        if (connection is null) return OpResult.Fail("Agent is offline.");

        var now = clock.GetUtcNow();
        var env = new CommandEnvelope(Guid.NewGuid(), s.Id, s.EnvironmentName, now, now + CommandLifetime,
            Guid.NewGuid().ToString("N"), s.Mode);
        cache.Set(CacheKey(env.CommandId), s.Id, CommandLifetime + TimeSpan.FromMinutes(5));
        await hub.Clients.Client(connection).RunDiscovery(env);
        return OpResult.Success();
    }

    public CommandEnvelope NewEnvelope(Server s, TimeSpan? lifetime = null)
    {
        var now = clock.GetUtcNow();
        return new CommandEnvelope(Guid.NewGuid(), s.Id, s.EnvironmentName ?? "", now, now + (lifetime ?? CommandLifetime), Guid.NewGuid().ToString("N"), s.Mode);
    }

    private string? Connection(Server s) => s.EnvironmentName is null || s.AgentStatus == AgentStatus.Revoked ? null : tracker.GetConnectionId(s.Id);

    public async Task<OpResult> SendManifestAsync(Server s, CommandEnvelope env, ManifestRequest req)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).GetManifest(env, req); return OpResult.Success();
    }

    public async Task<OpResult> SendMaskedFileAsync(Server s, CommandEnvelope env, MaskedFileRequest req)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).GetMaskedFile(env, req); return OpResult.Success();
    }

    public async Task<OpResult> SendDeployAsync(Server s, CommandEnvelope env, DeployCommand cmd)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).Deploy(env, cmd); return OpResult.Success();
    }

    public async Task<OpResult> SendRollbackAsync(Server s, CommandEnvelope env, RollbackCommand cmd)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).RollbackRun(env, cmd); return OpResult.Success();
    }

    public async Task<OpResult> SendPoolActionAsync(Server s, CommandEnvelope env, PoolActionCommand cmd)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).PoolAction(env, cmd); return OpResult.Success();
    }

    public async Task<OpResult> SendUpdatePoolAsync(Server s, CommandEnvelope env, UpdatePoolCommand cmd)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).UpdatePool(env, cmd); return OpResult.Success();
    }

    public async Task<OpResult> SendIisOpAsync(Server s, CommandEnvelope env, IisOpCommand cmd)
    {
        var c = Connection(s); if (c is null) return OpResult.Fail($"The agent on {s.Name} is offline.");
        await hub.Clients.Client(c).IisOperation(env, cmd); return OpResult.Success();
    }

    public bool IsConnected(Guid serverId) => tracker.IsConnected(serverId);

    /// <summary>True once per issued command id for that server.</summary>
    public bool ConsumeIssued(Guid commandId, Guid serverId)
    {
        var key = CacheKey(commandId);
        if (cache.TryGetValue(key, out Guid owner) && owner == serverId)
        {
            cache.Remove(key);
            return true;
        }
        return false;
    }

    public async Task DisconnectAsync(Guid serverId, string reason)
    {
        var connection = tracker.GetConnectionId(serverId);
        if (connection is not null)
        {
            try { await hub.Clients.Client(connection).Disconnect(reason); } catch { /* agent may already be gone */ }
        }
    }

    private static string CacheKey(Guid id) => $"cmd:{id:N}";
}
