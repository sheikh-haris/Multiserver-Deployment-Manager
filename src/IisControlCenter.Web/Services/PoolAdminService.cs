using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Deployments;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record PoolRow(
    string Name, string State, string Identity, string? Runtime, string Pipeline, PoolConfig? Config,
    IReadOnlyList<string> Sites, IReadOnlyList<string> Components);

public sealed record PoolPage(Server Server, IReadOnlyList<PoolRow> Pools, bool CanAct, string? WhyNot, bool AgentOnline);

public sealed record PoolActionResult(string Pool, bool Ok, string Message, string? StateBefore, string? StateAfter);

/// <summary>
/// Start, stop, recycle and edit app pools on one server. Needs the Server Operator (for that environment) or Platform Admin role,
/// a server in Managed mode (portal AND local switch on the server), and an online agent. Every attempt is audited with before and after.
/// </summary>
public sealed class PoolAdminService(
    IDbContextFactory<AppDbContext> dbf,
    AgentCommandService commands,
    PendingAgentResults pending,
    AuditService audit,
    ServerEventBus bus)
{
    public const int MaxPoolsPerCall = 50;
    private static readonly JsonSerializerOptions AuditJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(60);

    private static string? WhyNot(Server s, bool canManage, bool online)
    {
        if (s.EnvironmentName is null) return "The server is not approved yet.";
        if (!canManage) return "You can view app pools but not change them. This needs the Server Operator role for this environment, or Platform Admin.";
        if (s.Mode != ServerMode.Managed) return "The server is in ReadOnly mode. A Platform Admin can switch it to Managed, if the agent was installed with Managed mode allowed.";
        if (!online) return "The agent is offline.";
        return null;
    }

    public async Task<PoolPage?> GetAsync(Actor actor, Guid serverId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serverId && !x.IsDeleted);
        if (s is null || !actor.Can(Permission.ViewServers, s.EnvironmentName)) return null;

        var pools = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).OrderBy(p => p.Name).ToListAsync();
        var sites = await db.IisSites.AsNoTracking().Where(x => x.ServerId == serverId).Include(x => x.Applications).ToListAsync();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ServerId == serverId).ToListAsync();
        var comps = await db.Components.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);

        var rows = pools.Select(p =>
        {
            var usedBySites = sites.Where(x => string.Equals(x.AppPoolName, p.Name, StringComparison.OrdinalIgnoreCase)
                                            || x.Applications.Any(a => string.Equals(a.AppPoolName, p.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Name).OrderBy(x => x).ToList();
            var usedByComps = targets.Where(t => string.Equals(t.AppPoolName, p.Name, StringComparison.OrdinalIgnoreCase) && comps.ContainsKey(t.ComponentId))
                .Select(t => $"{projects.GetValueOrDefault(comps[t.ComponentId].ProjectId, "?")} / {comps[t.ComponentId].Name}").Distinct().OrderBy(x => x).ToList();
            return new PoolRow(p.Name, p.State, p.IdentityType, p.RuntimeVersion, p.PipelineMode,
                string.IsNullOrEmpty(p.ConfigJson) ? null : JsonSerializer.Deserialize<PoolConfig>(p.ConfigJson), usedBySites, usedByComps);
        }).ToList();

        var online = commands.IsConnected(serverId);
        var why = WhyNot(s, actor.Can(Permission.ManageIis, s.EnvironmentName), online);
        return new PoolPage(s, rows, why is null, why, online);
    }

    private async Task<(Server? server, string? error)> PrepareAsync(AppDbContext db, Actor actor, Guid serverId, string auditAction, string? pool)
    {
        var s = await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serverId && !x.IsDeleted);
        if (s is null) return (null, "Server not found.");
        if (!actor.Can(Permission.ManageIis, s.EnvironmentName))
        {
            await audit.WriteAsync(actor, new AuditRecord(auditAction, "AppPool", pool, serverId, s.EnvironmentName, Result: "Denied"));
            throw new ForbiddenException("You do not have permission to manage IIS on this server.");
        }
        var why = WhyNot(s, true, commands.IsConnected(serverId));
        return why is null ? (s, null) : (s, why);
    }

    public async Task<IReadOnlyList<PoolActionResult>> ActAsync(Actor actor, Guid serverId, IReadOnlyList<string> pools, string action, bool confirmed, CancellationToken ct = default)
    {
        if (PoolRules.ValidateAction(action) is { } bad) return [new("", false, bad, null, null)];
        pools = pools.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pools.Count == 0) return [new("", false, "Select at least one app pool.", null, null)];
        if (pools.Count > MaxPoolsPerCall) return [new("", false, $"Select at most {MaxPoolsPerCall} pools at a time.", null, null)];

        await using var db = await dbf.CreateDbContextAsync(ct);
        var (server, error) = await PrepareAsync(db, actor, serverId, "Pool." + action, null);
        if (server is null || error is not null) return [new("", false, error ?? "Server not found.", null, null)];
        var prod = server.EnvironmentName == EnvironmentNames.Prod;
        if (prod && action != PoolActions.Start && !confirmed) return [new("", false, "Production: confirm this action first.", null, null)];

        var known = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).Select(p => p.Name).ToListAsync(ct);
        var results = new List<PoolActionResult>();
        foreach (var pool in pools)
        {
            if (!known.Contains(pool, StringComparer.OrdinalIgnoreCase)) { results.Add(new(pool, false, "This pool is not in the last discovery. Refresh discovery first.", null, null)); continue; }
            var env = commands.NewEnvelope(server, TimeSpan.FromMinutes(2));
            var wait = pending.ExpectPool(env.CommandId, serverId);
            var sent = await commands.SendPoolActionAsync(server, env, new PoolActionCommand(pool, action));
            PoolOpResult? r = null; string? err = sent.Error;
            if (sent.Ok)
            {
                try { r = await wait.WaitAsync(AgentTimeout, ct); err = r.Ok ? null : r.Error; }
                catch (TimeoutException) { pending.Forget(env.CommandId); err = "The agent did not answer in time."; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { err = "The agent did not answer in time."; }
            }
            else pending.Forget(env.CommandId);

            await audit.WriteAsync(actor, new AuditRecord("Pool." + action, "AppPool", pool, serverId, server.EnvironmentName,
                Before: r?.StateBefore, After: r?.StateAfter, Result: err is null ? "Success" : "Failed", Detail: err));
            if (r?.StateAfter is { } st) await SaveStateAsync(serverId, pool, st, r.After);
            results.Add(err is null
                ? new(pool, true, r!.Changed ? $"{r.StateBefore} → {r.StateAfter}" : $"Already {r.StateAfter}.", r.StateBefore, r.StateAfter)
                : new(pool, false, err, r?.StateBefore, r?.StateAfter));
        }
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        return results;
    }

    public async Task<OpResult> UpdateAsync(Actor actor, Guid serverId, string pool, PoolConfig change, PoolConfig expected, bool confirmed, CancellationToken ct = default)
    {
        if (PoolRules.ValidateName(pool) is { } n) return OpResult.Fail(n);
        var problems = PoolRules.Validate(change);
        if (problems.Count > 0) return OpResult.Fail(string.Join(" ", problems));

        await using var db = await dbf.CreateDbContextAsync(ct);
        var (server, error) = await PrepareAsync(db, actor, serverId, "Pool.Update", pool);
        if (server is null || error is not null) return OpResult.Fail(error ?? "Server not found.");
        if (server.EnvironmentName == EnvironmentNames.Prod && !confirmed) return OpResult.Fail("Production: confirm this change first.");
        if (!await db.IisAppPools.AnyAsync(p => p.ServerId == serverId && p.Name == pool, ct)) return OpResult.Fail("This pool is not in the last discovery. Refresh discovery first.");

        var env = commands.NewEnvelope(server, TimeSpan.FromMinutes(2));
        var wait = pending.ExpectPool(env.CommandId, serverId);
        var sent = await commands.SendUpdatePoolAsync(server, env, new UpdatePoolCommand(pool, change, expected));
        PoolOpResult? r = null; string? err = sent.Error;
        if (sent.Ok)
        {
            try { r = await wait.WaitAsync(AgentTimeout, ct); err = r.Ok ? null : r.Error; }
            catch (TimeoutException) { pending.Forget(env.CommandId); err = "The agent did not answer in time."; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { err = "The agent did not answer in time."; }
        }
        else pending.Forget(env.CommandId);

        await audit.WriteAsync(actor, new AuditRecord("Pool.Update", "AppPool", pool, serverId, server.EnvironmentName,
            Before: r?.Before is null ? null : JsonSerializer.Serialize(Only(r.Before, change), AuditJson),
            After: r?.After is null ? null : JsonSerializer.Serialize(Only(r.After, change), AuditJson),
            Result: err is null ? "Success" : "Failed", Detail: err ?? (r!.Changed ? null : "Nothing changed: the pool already had these values.")));
        if (err is not null) return OpResult.Fail(err);
        if (r!.After is not null) await SaveStateAsync(serverId, pool, r.StateAfter ?? "", r.After);
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    /// <summary>The audit record shows only the fields the person changed, not the whole pool.</summary>
    private static PoolConfig Only(PoolConfig full, PoolConfig change) => new()
    {
        ManagedRuntimeVersion = change.ManagedRuntimeVersion is null ? null : full.ManagedRuntimeVersion, PipelineMode = change.PipelineMode is null ? null : full.PipelineMode,
        Enable32Bit = change.Enable32Bit is null ? null : full.Enable32Bit, StartMode = change.StartMode is null ? null : full.StartMode,
        IdentityType = change.IdentityType is null ? null : full.IdentityType, LoadUserProfile = change.LoadUserProfile is null ? null : full.LoadUserProfile,
        IdleTimeoutMinutes = change.IdleTimeoutMinutes is null ? null : full.IdleTimeoutMinutes, QueueLength = change.QueueLength is null ? null : full.QueueLength,
        RecycleIntervalMinutes = change.RecycleIntervalMinutes is null ? null : full.RecycleIntervalMinutes, RecycleTimes = change.RecycleTimes is null ? null : full.RecycleTimes,
        PrivateMemoryLimitKb = change.PrivateMemoryLimitKb is null ? null : full.PrivateMemoryLimitKb, RapidFailProtection = change.RapidFailProtection is null ? null : full.RapidFailProtection,
        RapidFailMaxCrashes = change.RapidFailMaxCrashes is null ? null : full.RapidFailMaxCrashes,
    };

    /// <summary>
    /// Writes what the agent just reported into the stored snapshot, so the portal's own change is not later reported as "drift".
    /// The next discovery confirms it.
    /// </summary>
    private async Task SaveStateAsync(Guid serverId, string pool, string state, PoolConfig? config)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.IisAppPools.FirstOrDefaultAsync(p => p.ServerId == serverId && p.Name == pool);
        if (row is null) return;
        row.State = state;
        if (config is not null)
        {
            row.ConfigJson = JsonSerializer.Serialize(config);
            row.IdentityType = config.IdentityType ?? row.IdentityType; row.PipelineMode = config.PipelineMode ?? row.PipelineMode;
            row.RuntimeVersion = string.IsNullOrEmpty(config.ManagedRuntimeVersion) ? null : config.ManagedRuntimeVersion;
        }
        await db.SaveChangesAsync();
    }
}
