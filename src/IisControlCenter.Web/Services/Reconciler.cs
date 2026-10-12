using IisControlCenter.Core.Discovery;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Targets;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

/// <summary>
/// Compares what IIS reported with what the portal expects. Logs changes made outside the portal as drift,
/// marks links Missing (deleted or renamed in IIS) or Drifted (folder or pool changed), and brings them back
/// to Active when IIS matches again. Deployments are blocked for any target that is not Active.
/// </summary>
public sealed class Reconciler(IDbContextFactory<AppDbContext> dbf, AuditService audit, ServerEventBus bus, TimeProvider clock)
{
    /// <summary>Called right after a new discovery snapshot was stored.</summary>
    public async Task AfterDiscoveryAsync(Guid serverId, bool hadPreviousSnapshot,
        IReadOnlyCollection<IisSite> oldSites, IReadOnlyCollection<IisAppPool> oldPools, IReadOnlyCollection<IisCertificate> oldCerts)
    {
        await using var db = await dbf.CreateDbContextAsync();
        if (hadPreviousSnapshot)
        {
            var newSites = await db.IisSites.AsNoTracking().Where(s => s.ServerId == serverId).Include(s => s.Bindings).Include(s => s.Applications).AsSplitQuery().ToListAsync();
            var newPools = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).ToListAsync();
            var newCerts = await db.IisCertificates.AsNoTracking().Where(c => c.ServerId == serverId).ToListAsync();
            var items = SnapshotDiffer.Diff(oldSites, newSites, oldPools, newPools, oldCerts, newCerts);
            var now = clock.GetUtcNow();
            foreach (var i in items)
                db.DriftEvents.Add(new DriftEvent
                {
                    Id = Guid.NewGuid(), ServerId = serverId, ObjectType = i.ObjectType, ObjectRef = Trunc(i.ObjectRef, 500)!, Kind = i.Kind,
                    Before = Trunc(i.Before, 2000), After = Trunc(i.After, 2000), DetectedAt = now,
                });
            if (items.Count > 0)
            {
                await db.SaveChangesAsync();
                await audit.WriteSystemAsync(new AuditRecord("Drift.Detected", "Server", serverId.ToString(), serverId, After: $"{items.Count} change(s) made outside the portal"));
            }
        }
        await VerifyServerAsync(serverId);
    }

    /// <summary>Checks every target of a server against the latest stored snapshot. Returns how many targets changed status.</summary>
    public async Task<int> VerifyServerAsync(Guid serverId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId);
        if (server?.LastDiscoveryAt is null) return 0;      // nothing to compare with: never mark Missing from no data

        var sites = await db.IisSites.Where(s => s.ServerId == serverId).Include(s => s.Applications).AsSplitQuery().ToListAsync();
        var targets = await db.DeploymentTargets.Where(t => t.ServerId == serverId && t.Status != TargetStatus.Disabled).ToListAsync();
        var names = await TargetNamesAsync(db, targets.Select(t => t.ComponentId).Distinct().ToList());
        var now = clock.GetUtcNow();
        var changed = 0;

        foreach (var t in targets)
        {
            var site = sites.FirstOrDefault(s => s.IisSiteId == t.IisSiteId);
            var r = TargetVerifier.Verify(t, site);
            t.LastVerifiedAt = now;
            if (r.Status == t.Status && r.Detail == t.StatusDetail) continue;

            var before = t.Status;
            t.Status = r.Status;
            t.StatusDetail = r.Detail;
            if (r.Status == before) continue;                // same status, only the wording changed

            changed++;
            var label = $"{names.GetValueOrDefault(t.ComponentId, "?")} / {t.EnvironmentName}";
            db.DriftEvents.Add(new DriftEvent
            {
                Id = Guid.NewGuid(), ServerId = serverId, ObjectType = "Target", ObjectRef = Trunc(label, 500)!, TargetId = t.Id,
                Kind = r.Status == TargetStatus.Missing ? DriftKind.Deleted : DriftKind.Changed,
                Before = Trunc(r.Before ?? before.ToString(), 2000), After = Trunc(r.Status == TargetStatus.Active ? "Active again" : r.Detail, 2000),
                DetectedAt = now,
                // Returning to normal needs no human action.
                AcknowledgedBy = r.Status == TargetStatus.Active ? "System" : null, AcknowledgedAt = r.Status == TargetStatus.Active ? now : null,
            });
        }

        // Keep the discovery rows' mapping status in step with the links.
        var linked = (await db.DeploymentTargets.AsNoTracking().Where(t => t.ServerId == serverId).Select(t => t.IisSiteId).ToListAsync()).ToHashSet();
        foreach (var s in sites) s.MappingStatus = linked.Contains(s.IisSiteId) ? MappingStatus.Mapped : MappingStatus.Unassigned;

        await db.SaveChangesAsync();
        if (changed > 0)
        {
            await audit.WriteSystemAsync(new AuditRecord("Target.Reconcile", "Server", serverId.ToString(), serverId, After: $"{changed} target(s) changed status"));
            bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        }
        return changed;
    }

    private static async Task<Dictionary<Guid, string>> TargetNamesAsync(AppDbContext db, List<Guid> componentIds) =>
        await (from c in db.Components.AsNoTracking() join p in db.Projects.AsNoTracking() on c.ProjectId equals p.Id where componentIds.Contains(c.Id)
               select new { c.Id, Name = p.Name + " / " + c.Name }).ToDictionaryAsync(x => x.Id, x => x.Name);

    private static string? Trunc(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];
}
