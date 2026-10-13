using System.Text;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

/// <summary>
/// CSV exports for people who report on the platform. Every export is checked against the viewer's permissions and written to the audit log.
/// Values that start with = + - @ are neutralised so a spreadsheet cannot run them as formulas.
/// </summary>
public sealed class ReportService(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    private const int MaxRows = 50_000;
    private static string Q(string? v) => AuditQueries.Csv(v);
    private static string Line(params string?[] cells) => string.Join(',', cells.Select(Q));

    private static bool SeesAll(Actor a) => a.Can(Permission.ViewAllProjects) || a.Can(Permission.ManageProjects);

    /// <summary>Deployments in a period. Platform Admin and Auditor see everything; others see only projects and environments they can view.</summary>
    public async Task<string> DeploymentHistoryCsvAsync(Actor actor, DateTimeOffset from, DateTimeOffset to, Guid? projectId = null, string? environment = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var reqs = await db.DeploymentRequests.AsNoTracking().Where(r => r.RequestedAt >= from && r.RequestedAt < to)
            .OrderByDescending(r => r.RequestedAt).Take(MaxRows).ToListAsync();
        var comps = await db.Components.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var pkgs = await db.Packages.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var ids = reqs.Select(r => r.Id).ToList();
        var approvers = (await db.Approvals.AsNoTracking().Where(a => ids.Contains(a.RequestId) && a.Decision == ApprovalDecision.Approved).ToListAsync())
            .GroupBy(a => a.RequestId).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(a => a.ApproverName + (a.IsSelfApproval ? " (self)" : ""))));
        var runs = (await db.DeploymentRuns.AsNoTracking().Where(r => ids.Contains(r.RequestId)).ToListAsync()).GroupBy(r => r.RequestId).ToDictionary(g => g.Key, g => g.ToList());

        var sb = new StringBuilder("RequestedAt,Project,Component,Environment,Type,Status,Package,Sha256,Source,RequestedBy,Approvers,DirectToProd,SelfApproved,Targets,DurationSeconds,Detail\n");
        var n = 0;
        foreach (var r in reqs)
        {
            var c = comps[r.ComponentId];
            if (projectId is { } p && c.ProjectId != p) continue;
            if (!string.IsNullOrEmpty(environment) && !string.Equals(r.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase)) continue;
            if (!SeesAll(actor) && !actor.Access.IsProjectOwner(c.ProjectId) && !actor.Access.EnvironmentsFor(ProjectPermission.View, c.ProjectId).Contains(r.EnvironmentName)) continue;
            var pk = pkgs.GetValueOrDefault(r.PackageId);
            var rs = runs.GetValueOrDefault(r.Id) ?? [];
            sb.AppendLine(Line(r.RequestedAt.ToString("O"), projects.GetValueOrDefault(c.ProjectId), c.Name, r.EnvironmentName, r.Type.ToString(), r.Status.ToString(), pk?.FileName, pk?.Sha256,
                r.SourceReference, r.RequestedBy, approvers.GetValueOrDefault(r.Id), r.IsDirectToProd ? "yes" : "no", r.SelfApproved ? "yes" : "no", rs.Count.ToString(),
                (rs.Sum(x => x.DurationMs) / 1000.0).ToString("0.#"), r.StatusDetail));
            n++;
        }
        await audit.WriteAsync(actor, new AuditRecord("Report.Export", "DeploymentHistory", null, After: $"{n} rows; {from:yyyy-MM-dd}..{to:yyyy-MM-dd}"));
        return sb.ToString();
    }

    /// <summary>Servers with their sites, bindings, folders and pools (last discovery). Needs permission to view servers.</summary>
    public async Task<string> InventoryCsvAsync(Actor actor)
    {
        if (!actor.Can(Permission.ViewServers) && !actor.Can(Permission.ViewDiscoveryReport)) throw new ForbiddenException("You do not have permission to view servers.");
        await using var db = await dbf.CreateDbContextAsync();
        var servers = await db.Servers.AsNoTracking().Where(s => !s.IsDeleted && s.EnvironmentName != null).OrderBy(s => s.EnvironmentName).ThenBy(s => s.Name).ToListAsync();
        var sb = new StringBuilder("Server,Environment,Mode,Agent,AgentVersion,LastSeen,Site,SiteState,Application,Folder,Pool,PoolRuntime,PoolIdentity,HttpBindings,HttpsBindings,Mapping\n");
        var n = 0;
        foreach (var s in servers)
        {
            if (!actor.Can(Permission.ViewServers, s.EnvironmentName) && !actor.Can(Permission.ViewDiscoveryReport, s.EnvironmentName)) continue;
            var pools = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == s.Id).ToDictionaryAsync(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var sites = await db.IisSites.AsNoTracking().Where(x => x.ServerId == s.Id).Include(x => x.Bindings).Include(x => x.Applications).OrderBy(x => x.Name).ToListAsync();
            foreach (var site in sites)
            {
                var http = string.Join("; ", site.Bindings.Where(b => b.Protocol == "http").Select(b => $"{b.Host}:{b.Port}"));
                var https = string.Join("; ", site.Bindings.Where(b => b.Protocol == "https").Select(b => $"{b.Host}:{b.Port}"));
                foreach (var app in site.Applications.OrderBy(a => a.Path))
                {
                    var pool = app.AppPoolName is { } pn && pools.TryGetValue(pn, out var pp) ? pp : null;
                    sb.AppendLine(Line(s.Name, s.EnvironmentName, s.Mode.ToString(), s.AgentStatus.ToString(), s.AgentVersion, s.LastSeenAt?.ToString("O"), site.Name, site.State,
                        app.Path, app.PhysicalPath, app.AppPoolName, pool?.RuntimeVersion, pool?.IdentityType, http, https, site.MappingStatus.ToString()));
                    n++;
                }
            }
        }
        await audit.WriteAsync(actor, new AuditRecord("Report.Export", "Inventory", null, After: $"{n} rows"));
        return sb.ToString();
    }

    /// <summary>Who holds which role, with expiry. Needs the audit permission (Auditor, Platform Admin).</summary>
    public async Task<string> AccessCsvAsync(Actor actor, bool includeRevoked = false)
    {
        if (!actor.Can(Permission.ViewAudit) && !actor.Can(Permission.ManageRoles)) throw new ForbiddenException("You do not have permission to view role assignments.");
        await using var db = await dbf.CreateDbContextAsync();
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var q = db.RoleAssignments.AsNoTracking().AsQueryable();
        if (!includeRevoked) q = q.Where(a => a.RevokedAt == null);
        var rows = await q.OrderBy(a => a.Scope).ThenBy(a => a.PrincipalName).Take(MaxRows).ToListAsync();
        var now = clock.GetUtcNow();
        var sb = new StringBuilder("Principal,Type,PrincipalId,Scope,Project,Role,Environment,GrantedBy,GrantedAt,ExpiresAt,Status,RevokedBy\n");
        foreach (var a in rows)
        {
            var status = a.RevokedAt is not null ? "Revoked" : a.ExpiresAt is { } e && e < now ? "Expired" : a.ExpiresAt is { } e2 && e2 < now.AddDays(14) ? "Expires soon" : "Active";
            sb.AppendLine(Line(a.PrincipalName, a.PrincipalType.ToString(), a.PrincipalId, a.Scope.ToString(), a.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, a.Role.ToString(), a.EnvironmentName ?? "(all)",
                a.GrantedBy, a.GrantedAt.ToString("O"), a.ExpiresAt?.ToString("O"), status, a.RevokedBy));
        }
        await audit.WriteAsync(actor, new AuditRecord("Report.Export", "Access", null, After: $"{rows.Count} rows"));
        return sb.ToString();
    }
}
