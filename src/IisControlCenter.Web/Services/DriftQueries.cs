using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record DriftRow(Guid Id, Guid ServerId, string ServerName, string? Environment, string ObjectType, string ObjectRef, DriftKind Kind,
    string? Before, string? After, DateTimeOffset DetectedAt, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt);

public sealed class DriftQueries(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    private static void Require(Actor a)
    {
        if (!a.Can(Permission.ViewServers)) throw new ForbiddenException("You do not have permission to view drift.");
    }

    public async Task<(IReadOnlyList<DriftRow> Rows, int Total)> ListAsync(Actor actor, Guid? serverId, bool onlyOpen, string? search, int page, int pageSize)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var allowed = actor.Access.AllowedEnvironments(Permission.ViewServers);
        var q = from d in db.DriftEvents.AsNoTracking()
                join s in db.Servers.AsNoTracking() on d.ServerId equals s.Id
                select new { d, s.Name, s.EnvironmentName };
        if (allowed is not null) { var l = allowed.ToList(); q = q.Where(x => x.EnvironmentName != null && l.Contains(x.EnvironmentName)); }
        if (serverId is not null) q = q.Where(x => x.d.ServerId == serverId);
        if (onlyOpen) q = q.Where(x => x.d.AcknowledgedAt == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(x => x.d.ObjectRef.Contains(t) || x.Name.Contains(t) || (x.d.After != null && x.d.After.Contains(t)) || (x.d.Before != null && x.d.Before.Contains(t)));
        }
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(x => x.d.DetectedAt).ThenBy(x => x.d.ObjectRef).Skip(page * pageSize).Take(pageSize).ToListAsync();
        return (rows.Select(x => new DriftRow(x.d.Id, x.d.ServerId, x.Name, x.EnvironmentName, x.d.ObjectType, x.d.ObjectRef, x.d.Kind, x.d.Before, x.d.After,
            x.d.DetectedAt, x.d.AcknowledgedBy, x.d.AcknowledgedAt)).ToList(), total);
    }

    public async Task<OpResult> AcknowledgeAsync(Actor actor, Guid id)
    {
        if (!actor.Can(Permission.ManageTargets) && !actor.Can(Permission.TriggerDiscovery))
        {
            await audit.WriteAsync(actor, new AuditRecord("Drift.Acknowledge", "DriftEvent", id.ToString(), Result: "Denied"));
            throw new ForbiddenException("You do not have permission to acknowledge drift.");
        }
        await using var db = await dbf.CreateDbContextAsync();
        var d = await db.DriftEvents.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return OpResult.Fail("Not found.");
        var env = await db.Servers.AsNoTracking().Where(s => s.Id == d.ServerId).Select(s => s.EnvironmentName).FirstOrDefaultAsync();
        if (!actor.Can(Permission.ManageTargets) && !actor.Can(Permission.TriggerDiscovery, env))
            throw new ForbiddenException("You do not have permission for this environment.");
        if (d.AcknowledgedAt is not null) return OpResult.Success();
        d.AcknowledgedBy = actor.UserName; d.AcknowledgedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Drift.Acknowledge", "DriftEvent", id.ToString(), d.ServerId, env, After: $"{d.ObjectType} {d.ObjectRef}"));
        return OpResult.Success();
    }

    public async Task<(int OpenDrift, int BlockedTargets)> CountsAsync(Actor actor)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var allowed = actor.Access.AllowedEnvironments(Permission.ViewServers)?.ToList();
        var drift = from d in db.DriftEvents.AsNoTracking() join s in db.Servers.AsNoTracking() on d.ServerId equals s.Id where d.AcknowledgedAt == null select new { s.EnvironmentName };
        var blocked = from t in db.DeploymentTargets.AsNoTracking() where t.Status == TargetStatus.Missing || t.Status == TargetStatus.Drifted select new { t.EnvironmentName };
        if (allowed is not null)
        {
            drift = drift.Where(x => x.EnvironmentName != null && allowed.Contains(x.EnvironmentName));
            blocked = blocked.Where(x => allowed.Contains(x.EnvironmentName));
        }
        return (await drift.CountAsync(), await blocked.CountAsync());
    }
}
