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
    ServerEventBus bus,
    TimeProvider clock,
    IisOpsService iisOps)
{
    public const int MaxPoolsPerCall = 50;
    private static readonly SemaphoreSlim DecideGate = new(1, 1);
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

    /// <summary>Production: stopping pools and editing them needs a second person. Recycle needs the typed confirmation. Start is free.</summary>
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
        if (prod && action == PoolActions.Stop) return [new("", false, "Stopping app pools in Production needs a second person's approval. Create a request instead.", null, null)];
        if (prod && action == PoolActions.Recycle && !confirmed) return [new("", false, "Production: confirm this action first.", null, null)];
        return await ExecuteActionAsync(db, actor, server, pools, action, null, ct);
    }

    private async Task<List<PoolActionResult>> ExecuteActionAsync(AppDbContext db, Actor actor, Server server, IReadOnlyList<string> pools, string action, string? detailNote, CancellationToken ct)
    {
        var serverId = server.Id;
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
                Before: r?.StateBefore, After: r?.StateAfter, Result: err is null ? "Success" : "Failed", Detail: err ?? detailNote));
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
        if (server.EnvironmentName == EnvironmentNames.Prod) return OpResult.Fail("Changing app pool settings in Production needs a second person's approval. Create a request instead.");
        return await ExecuteUpdateAsync(db, actor, server, pool, change, expected, null, ct);
    }

    private async Task<OpResult> ExecuteUpdateAsync(AppDbContext db, Actor actor, Server server, string pool, PoolConfig change, PoolConfig expected, string? detailNote, CancellationToken ct)
    {
        var serverId = server.Id;
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
            Result: err is null ? "Success" : "Failed", Detail: err ?? (r!.Changed ? detailNote : "Nothing changed: the pool already had these values.")));
        if (err is not null) return OpResult.Fail(err);
        if (r!.After is not null) await SaveStateAsync(serverId, pool, r.StateAfter ?? "", r.After);
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    // =========================== Production: second person ===========================

    public static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(24);

    public async Task<OpResult<Guid>> RequestStopAsync(Actor actor, Guid serverId, IReadOnlyList<string> pools, string? reason, CancellationToken ct = default)
    {
        pools = pools.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pools.Count == 0) return OpResult<Guid>.Fail("Select at least one app pool.");
        if (pools.Count > MaxPoolsPerCall) return OpResult<Guid>.Fail($"Select at most {MaxPoolsPerCall} pools at a time.");
        foreach (var p in pools) if (PoolRules.ValidateName(p) is { } e) return OpResult<Guid>.Fail(e);
        return await CreateRequestAsync(actor, serverId, PoolRequestKind.Stop, pools, null, null, reason, ct);
    }

    public async Task<OpResult<Guid>> RequestEditAsync(Actor actor, Guid serverId, string pool, PoolConfig change, PoolConfig expected, string? reason, CancellationToken ct = default)
    {
        if (PoolRules.ValidateName(pool) is { } n) return OpResult<Guid>.Fail(n);
        var problems = PoolRules.Validate(change);
        if (problems.Count > 0) return OpResult<Guid>.Fail(string.Join(" ", problems));
        return await CreateRequestAsync(actor, serverId, PoolRequestKind.Edit, [pool], change, expected, reason, ct);
    }

    private async Task<OpResult<Guid>> CreateRequestAsync(Actor actor, Guid serverId, PoolRequestKind kind, IReadOnlyList<string> pools, PoolConfig? change, PoolConfig? expected, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return OpResult<Guid>.Fail("Write the reason.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (server, error) = await PrepareAsync(db, actor, serverId, "Pool.Request", null);
        if (server is null || error is not null) return OpResult<Guid>.Fail(error ?? "Server not found.");
        if (server.EnvironmentName != EnvironmentNames.Prod) return OpResult<Guid>.Fail("Only Production needs an approval. Outside Production, do it directly.");
        var known = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).Select(p => p.Name).ToListAsync(ct);
        var unknown = pools.FirstOrDefault(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null) return OpResult<Guid>.Fail($"'{unknown}' is not in the last discovery.");

        var now = clock.GetUtcNow();
        var req = new PoolChangeRequest
        {
            Id = Guid.NewGuid(), ServerId = serverId, EnvironmentName = server.EnvironmentName!, Kind = kind, PoolsJson = JsonSerializer.Serialize(pools),
            ChangeJson = change is null ? null : JsonSerializer.Serialize(change), ExpectedJson = expected is null ? null : JsonSerializer.Serialize(expected),
            Reason = reason.Trim(), RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedAt = now, ExpiresAt = now + RequestLifetime,
        };
        db.PoolChangeRequests.Add(req);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, new AuditRecord("Pool.Request", "PoolChangeRequest", req.Id.ToString(), serverId, server.EnvironmentName,
            After: $"{kind}: {string.Join(", ", pools)}" + (change is null ? "" : "; " + Summarize(change)), Detail: reason.Trim()));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        return OpResult<Guid>.Success(req.Id);
    }

    public static string Summarize(PoolConfig c) => System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(c, AuditJson), "[{}\"]", "").Replace(",", ", ");

    public sealed record PoolRequestRow(PoolChangeRequest Request, string ServerName, IReadOnlyList<string> Pools, string? ChangeSummary, bool CanDecide, bool CanCancel, string? WhyCannotDecide);

    /// <summary>Requests for environments the person may see. Pending first; decided ones from the last 14 days.</summary>
    public async Task<IReadOnlyList<PoolRequestRow>> RequestsAsync(Actor actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        foreach (var r in await db.PoolChangeRequests.Where(x => x.Status == PoolRequestStatus.Pending && x.ExpiresAt < now).ToListAsync()) r.Status = PoolRequestStatus.Expired;
        await db.SaveChangesAsync();
        var since = now.AddDays(-14);
        var list = await db.PoolChangeRequests.AsNoTracking().Where(x => x.Status == PoolRequestStatus.Pending || x.RequestedAt > since).OrderByDescending(x => x.RequestedAt).Take(200).ToListAsync();
        var names = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var rows = new List<PoolRequestRow>();
        foreach (var r in list)
        {
            if (!actor.Can(Permission.ViewServers, r.EnvironmentName)) continue;
            var mine = string.Equals(r.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase);
            var isOp = actor.Can(Permission.ManageIis, r.EnvironmentName);
            string? why = r.Status != PoolRequestStatus.Pending ? null : !isOp ? "You need the Server Operator role for Production, or Platform Admin." : mine ? "You requested this. A second person has to approve it." : null;
            rows.Add(new PoolRequestRow(r, names.GetValueOrDefault(r.ServerId, "?"), JsonSerializer.Deserialize<List<string>>(r.PoolsJson) ?? [],
                r.Summary ?? (r.ChangeJson is null ? null : Summarize(JsonSerializer.Deserialize<PoolConfig>(r.ChangeJson)!)),
                r.Status == PoolRequestStatus.Pending && why is null, r.Status == PoolRequestStatus.Pending && (mine || actor.Can(Permission.ManageServers)), why));
        }
        return rows;
    }

    public async Task<OpResult> DecideAsync(Actor actor, Guid requestId, bool approve, string? comment, CancellationToken ct = default)
    {
        if (!approve && string.IsNullOrWhiteSpace(comment)) return OpResult.Fail("Write the reason for rejecting.");
        await DecideGate.WaitAsync(ct);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var r = await db.PoolChangeRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct);
            if (r is null) return OpResult.Fail("Request not found.");
            var action = approve ? "Pool.Request.Approve" : "Pool.Request.Reject";
            if (!actor.Can(Permission.ManageIis, r.EnvironmentName))
            {
                await audit.WriteAsync(actor, new AuditRecord(action, "PoolChangeRequest", requestId.ToString(), r.ServerId, r.EnvironmentName, Result: "Denied"));
                throw new ForbiddenException("You need the Server Operator role for Production, or Platform Admin.");
            }
            if (string.Equals(r.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase))
            {
                await audit.WriteAsync(actor, new AuditRecord(action, "PoolChangeRequest", requestId.ToString(), r.ServerId, r.EnvironmentName, Result: "Denied", Detail: "Own request."));
                return OpResult.Fail("You requested this. A second person has to decide.");
            }
            var now = clock.GetUtcNow();
            if (r.Status == PoolRequestStatus.Pending && r.ExpiresAt < now) { r.Status = PoolRequestStatus.Expired; await db.SaveChangesAsync(ct); }
            if (r.Status != PoolRequestStatus.Pending) return OpResult.Fail($"This request is {r.Status}.");

            r.DecidedBy = actor.UserName; r.DecidedById = actor.UserId; r.DecidedAt = now; r.DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            if (!approve)
            {
                r.Status = PoolRequestStatus.Rejected;
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(actor, new AuditRecord(action, "PoolChangeRequest", requestId.ToString(), r.ServerId, r.EnvironmentName, Detail: comment));
                bus.Publish(new ServerChanged(r.ServerId, ServerChangeKind.Status));
                return OpResult.Success();
            }

            var server = await db.Servers.AsNoTracking().FirstAsync(s => s.Id == r.ServerId, ct);
            var why = WhyNot(server, true, commands.IsConnected(server.Id));
            if (why is not null) return OpResult.Fail(why);       // stays pending: try again when the agent is back

            var note = $"Approved by {actor.UserName}; requested by {r.RequestedBy}: {r.Reason}";
            string message; bool ok;
            var pools = JsonSerializer.Deserialize<List<string>>(r.PoolsJson) ?? [];
            if (r.Kind == PoolRequestKind.Iis)
            {
                var o = await iisOps.ExecuteApprovedAsync(db, actor, server, r, ct);
                ok = o.Ok; message = o.Ok ? (o.Result?.After ?? "Done.") : o.Error ?? "Failed.";
            }
            else if (r.Kind == PoolRequestKind.Stop)
            {
                var res = await ExecuteActionAsync(db, actor, server, pools, PoolActions.Stop, note, ct);
                ok = res.All(x => x.Ok); message = string.Join("; ", res.Select(x => $"{x.Pool}: {x.Message}"));
            }
            else
            {
                var o = await ExecuteUpdateAsync(db, actor, server, pools[0], JsonSerializer.Deserialize<PoolConfig>(r.ChangeJson!)!, JsonSerializer.Deserialize<PoolConfig>(r.ExpectedJson!)!, note, ct);
                ok = o.Ok; message = o.Ok ? $"{pools[0]} saved." : o.Error ?? "Failed.";
            }
            r.Status = ok ? PoolRequestStatus.Executed : PoolRequestStatus.Failed; r.ResultMessage = message;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, new AuditRecord(action, "PoolChangeRequest", requestId.ToString(), r.ServerId, r.EnvironmentName, Result: ok ? "Success" : "Failed",
                After: $"{r.Kind}: {string.Join(", ", pools)}", Detail: message));
            bus.Publish(new ServerChanged(r.ServerId, ServerChangeKind.Status));
            return ok ? OpResult.Success() : OpResult.Fail("Approved, but it failed: " + message);
        }
        finally { DecideGate.Release(); }
    }

    public async Task<OpResult> CancelRequestAsync(Actor actor, Guid requestId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var r = await db.PoolChangeRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null) return OpResult.Fail("Request not found.");
        if (!string.Equals(r.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase) && !actor.Can(Permission.ManageServers))
            throw new ForbiddenException("Only the requester (or a Platform Admin) can cancel this.");
        if (r.Status != PoolRequestStatus.Pending) return OpResult.Fail($"This request is {r.Status}.");
        r.Status = PoolRequestStatus.Cancelled; r.DecidedBy = actor.UserName; r.DecidedById = actor.UserId; r.DecidedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Pool.Request.Cancel", "PoolChangeRequest", requestId.ToString(), r.ServerId, r.EnvironmentName));
        bus.Publish(new ServerChanged(r.ServerId, ServerChangeKind.Status));
        return OpResult.Success();
    }

    public async Task<int> PendingCountAsync(Actor actor)
    {
        var rows = await RequestsAsync(actor);
        return rows.Count(r => r.CanDecide);
    }

    // =========================== project Deployers: recycle their own pool ===========================

    /// <summary>
    /// A Deployer can recycle (only recycle) the pool of a target of their project in the environments they deploy to. Never stop, never edit.
    /// Refused when the pool also serves anything else, because that would restart other people's applications.
    /// </summary>
    public async Task<PoolActionResult> RecycleForTargetAsync(Actor actor, Guid targetId, bool confirmed, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var t = await db.DeploymentTargets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetId, ct);
        if (t is null) return new("", false, "Target not found.", null, null);
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == t.ComponentId, ct);
        if (!actor.Access.CanOnProject(ProjectPermission.RecyclePool, comp.ProjectId, t.EnvironmentName))
        {
            await audit.WriteAsync(actor, new AuditRecord("Pool.Recycle", "AppPool", t.AppPoolName, t.ServerId, t.EnvironmentName, Result: "Denied", Detail: "Project role check"));
            throw new ForbiddenException("You need the Deployer role for this project and environment.");
        }
        if (t.Status != TargetStatus.Active) return new(t.AppPoolName, false, $"The target is {t.Status}. Fix the link first.", null, null);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == t.ServerId && !s.IsDeleted, ct);
        if (server is null) return new(t.AppPoolName, false, "Server not found.", null, null);
        var why = WhyNot(server, true, commands.IsConnected(server.Id));
        if (why is not null) return new(t.AppPoolName, false, why, null, null);
        if (server.EnvironmentName == EnvironmentNames.Prod && !confirmed) return new(t.AppPoolName, false, "Production: confirm this action first.", null, null);

        // Does anything else use this pool?
        var apps = await db.IisApplications.AsNoTracking()
            .Where(a => a.AppPoolName == t.AppPoolName && db.IisSites.Any(s => s.Id == a.IisSiteRowId && s.ServerId == t.ServerId))
            .Select(a => new { a.Path, SiteId = db.IisSites.Where(s => s.Id == a.IisSiteRowId).Select(s => s.IisSiteId).First(), SiteName = db.IisSites.Where(s => s.Id == a.IisSiteRowId).Select(s => s.Name).First() })
            .ToListAsync(ct);
        static string NormApp(string? p) { var v = string.IsNullOrWhiteSpace(p) ? "/" : p.Trim(); return v.Length > 1 ? v.TrimEnd('/') : v; }
        var others = apps.Where(a => !(a.SiteId == t.IisSiteId && string.Equals(NormApp(a.Path), NormApp(t.AppPath), StringComparison.OrdinalIgnoreCase))).ToList();
        var otherTargets = await db.DeploymentTargets.AsNoTracking().AnyAsync(x => x.ServerId == t.ServerId && x.AppPoolName == t.AppPoolName && x.Id != t.Id, ct);
        if (others.Count > 0 || otherTargets)
            return new(t.AppPoolName, false, $"This app pool also serves other applications ({string.Join(", ", others.Select(o => o.SiteName + (o.Path == "/" ? "" : o.Path)).Distinct())}). Ask a Server Operator to recycle it.", null, null);

        var res = await ExecuteActionAsync(db, actor, server, [t.AppPoolName], PoolActions.Recycle, "By a project Deployer for their own application.", ct);
        return res.Single();
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
