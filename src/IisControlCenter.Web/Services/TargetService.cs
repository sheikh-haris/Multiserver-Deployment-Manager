using IisControlCenter.Core.Discovery;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Projects;
using IisControlCenter.Core.Security;
using IisControlCenter.Core.Targets;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record Candidate(Guid ServerId, string ServerName, string? Environment, long IisSiteId, string SiteName, string AppPath,
    string? PhysicalPath, string? AppPoolName, IReadOnlyList<string> Hosts, bool IsMapped, string? MappedTo, MappingSuggestion Suggestion);

public sealed record LinkPreview(Candidate Item, IReadOnlyList<LinkViolation> Violations)
{
    public bool HasBlocking => Violations.Any(v => !v.Exceptionable);
    public bool NeedsException => !HasBlocking && Violations.Any(v => v.Exceptionable);
    public bool CanLink => Violations.Count == 0;
}

public sealed record LinkRequestRow(Guid Id, string ProjectName, string ComponentName, string EnvironmentName, string ServerName, string SiteName,
    string AppPath, string Reason, LinkRequestStatus Status, string RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, string? DecisionComment);

public sealed class TargetService(IDbContextFactory<AppDbContext> dbf, AuditService audit, Reconciler reconciler, ServerEventBus bus, TimeProvider clock)
{
    // ---------- who may see the onboarding list ----------

    private static bool IsAdmin(Actor a) => a.Can(Permission.ManageTargets);
    private static bool IsOwnerSomewhere(Actor a) => a.Access.ActiveAssignments.Any(r => r.Scope == RoleScope.Project && r.Role == Role.ProjectOwner);

    // ---------- candidates ----------

    public async Task<(IReadOnlyList<Candidate> Rows, int Total)> ListCandidatesAsync(Actor actor, Guid? serverId, string? search, bool onlyUnassigned, int page, int pageSize)
    {
        if (!IsAdmin(actor) && !IsOwnerSomewhere(actor)) throw new ForbiddenException("You do not have permission to see the onboarding list.");
        await using var db = await dbf.CreateDbContextAsync();
        var all = await BuildCandidatesAsync(db, actor, serverId);
        IEnumerable<Candidate> q = all;
        if (onlyUnassigned) q = q.Where(c => !c.IsMapped);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(c => c.SiteName.Contains(t, StringComparison.OrdinalIgnoreCase) || c.ServerName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (c.PhysicalPath ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || c.Hosts.Any(h => h.Contains(t, StringComparison.OrdinalIgnoreCase))
                || (c.AppPoolName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        var list = q.ToList();
        return (list.Skip(page * pageSize).Take(pageSize).ToList(), list.Count);
    }

    private async Task<List<Candidate>> BuildCandidatesAsync(AppDbContext db, Actor actor, Guid? serverId)
    {
        var servers = await db.Servers.AsNoTracking().Where(s => s.EnvironmentName != null && (serverId == null || s.Id == serverId)).ToListAsync();
        var ids = servers.Select(s => s.Id).ToList();
        var sites = await db.IisSites.AsNoTracking().Where(s => ids.Contains(s.ServerId)).Include(s => s.Bindings).Include(s => s.Applications).AsSplitQuery().ToListAsync();
        var links = await LoadLinksAsync(db, ids);

        var projects = await db.Projects.AsNoTracking().Where(p => p.IsActive).Include(p => p.Components).ToListAsync();
        if (!IsAdmin(actor))   // owners only get suggestions for their own projects
        {
            var mine = actor.Access.ActiveAssignments.Where(r => r.Role == Role.ProjectOwner && r.ProjectId != null).Select(r => r.ProjectId!.Value).ToHashSet();
            projects = projects.Where(p => mine.Contains(p.Id)).ToList();
        }
        var known = projects.Select(p => new KnownProject(p.Name, p.Components.Select(c => c.Name).ToList())).ToList();

        var rows = new List<Candidate>();
        foreach (var srv in servers.OrderBy(s => s.Name))
            foreach (var site in sites.Where(s => s.ServerId == srv.Id).OrderBy(s => s.Name))
            {
                var hosts = site.Bindings.Select(b => b.Host).Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).Distinct().ToList();
                foreach (var app in site.Applications.OrderBy(a => a.Path))
                {
                    var link = links.FirstOrDefault(l => l.ServerId == srv.Id && l.IisSiteId == site.IisSiteId
                        && string.Equals(LinkRules.NormalizeAppPath(l.AppPath), LinkRules.NormalizeAppPath(app.Path), StringComparison.OrdinalIgnoreCase));
                    var name = app.Path == "/" ? site.Name : $"{site.Name}{app.Path}";
                    rows.Add(new Candidate(srv.Id, srv.Name, srv.EnvironmentName, site.IisSiteId, site.Name, app.Path, app.PhysicalPath, app.AppPoolName, hosts,
                        link is not null, link is null ? null : $"{link.ProjectName} / {link.ComponentName}",
                        link is null ? MappingSuggester.Suggest(site.Name + (app.Path == "/" ? "" : "-" + app.Path.Trim('/')), hosts, app.PhysicalPath, known) : MappingSuggestion.None));
                }
            }
        return rows;
    }

    private static async Task<List<ExistingLinkEx>> LoadLinksAsync(AppDbContext db, List<Guid> serverIds) =>
        await (from t in db.DeploymentTargets.AsNoTracking()
               join c in db.Components.AsNoTracking() on t.ComponentId equals c.Id
               join p in db.Projects.AsNoTracking() on c.ProjectId equals p.Id
               where serverIds.Contains(t.ServerId)
               select new ExistingLinkEx(t.ServerId, t.Id, c.Id, c.Name, p.Id, p.Name, t.EnvironmentName, t.IisSiteId, t.AppPath, t.PhysicalPath, t.AppPoolName)).ToListAsync();

    private sealed record ExistingLinkEx(Guid ServerId, Guid TargetId, Guid ComponentId, string ComponentName, Guid ProjectId, string ProjectName,
        string EnvironmentName, long IisSiteId, string AppPath, string PhysicalPath, string AppPoolName)
    {
        public ExistingLink ToRule() => new(TargetId, ComponentId, ComponentName, ProjectId, ProjectName, EnvironmentName, IisSiteId, AppPath, PhysicalPath, AppPoolName);
    }

    // ---------- preview and link ----------

    public async Task<OpResult<LinkPreview>> PreviewAsync(Actor actor, Guid componentId, Guid serverId, long iisSiteId, string appPath)
    {
        if (!IsAdmin(actor) && !IsOwnerSomewhere(actor)) throw new ForbiddenException("You do not have permission to map IIS items.");
        await using var db = await dbf.CreateDbContextAsync();
        var ctx = await LoadContextAsync(db, componentId, serverId, iisSiteId, appPath, null);
        if (ctx.Error is not null) return OpResult<LinkPreview>.Fail(ctx.Error);
        var item = (await BuildCandidatesAsync(db, actor, serverId)).First(c => c.IisSiteId == iisSiteId && LinkRules.NormalizeAppPath(c.AppPath) == LinkRules.NormalizeAppPath(appPath));
        return OpResult<LinkPreview>.Success(new LinkPreview(item, ctx.Violations!));
    }

    private sealed record LinkContext(string? Error, Component? Component, Project? Project, Server? Server, IisSite? Site, IisApplication? App,
        LinkCandidate? Candidate, List<LinkViolation>? Violations);

    private async Task<LinkContext> LoadContextAsync(AppDbContext db, Guid componentId, Guid serverId, long iisSiteId, string appPath, Guid? excludeTargetId)
    {
        var component = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId);
        if (component is null) return new("Component not found.", null, null, null, null, null, null, null);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == component.ProjectId);
        if (!project.IsActive) return new("The project is inactive.", null, null, null, null, null, null, null);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId);
        if (server is null) return new("Server not found.", null, null, null, null, null, null, null);
        var site = await db.IisSites.AsNoTracking().Where(s => s.ServerId == serverId && s.IisSiteId == iisSiteId).Include(s => s.Applications).FirstOrDefaultAsync();
        if (site is null) return new("That site was not found in the last discovery.", null, null, null, null, null, null, null);
        var np = LinkRules.NormalizeAppPath(appPath);
        var app = site.Applications.FirstOrDefault(a => LinkRules.NormalizeAppPath(a.Path) == np);
        if (app is null) return new("That application was not found in the last discovery.", null, null, null, null, null, null, null);

        var existing = (await LoadLinksAsync(db, [serverId])).Where(l => l.TargetId != excludeTargetId).Select(l => l.ToRule()).ToList();
        // Targets of the same component on OTHER servers are fine; only this server's links matter for site/pool/path rules.
        // The component+environment+server rule needs links of the same component on this server, which are included above.
        var candidate = new LinkCandidate(componentId, project.Id, server.EnvironmentName ?? "", serverId, server.EnvironmentName, iisSiteId, np,
            app.PhysicalPath ?? "", app.AppPoolName ?? "");
        return new(null, component, project, server, site, app, candidate, LinkRules.Check(candidate, existing));
    }

    /// <summary>Platform Admin links an IIS item to a component. Blocking violations can never be waived; a pool conflict needs a recorded reason.</summary>
    public async Task<OpResult<Guid>> LinkAsync(Actor actor, Guid componentId, Guid serverId, long iisSiteId, string appPath, string? exceptionReason = null)
    {
        await RequireAdminAsync(actor, "Target.Link", componentId.ToString());
        return await LinkCoreAsync(actor.UserName, actor, componentId, serverId, iisSiteId, appPath, exceptionReason, "Target.Link");
    }

    private async Task<OpResult<Guid>> LinkCoreAsync(string by, Actor actorForAudit, Guid componentId, Guid serverId, long iisSiteId, string appPath, string? exceptionReason, string action)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var ctx = await LoadContextAsync(db, componentId, serverId, iisSiteId, appPath, null);
        if (ctx.Error is not null) return OpResult<Guid>.Fail(ctx.Error);

        var blocking = ctx.Violations!.Where(v => !v.Exceptionable).ToList();
        if (blocking.Count > 0)
        {
            await audit.WriteAsync(actorForAudit, new AuditRecord(action, "Component", componentId.ToString(), serverId, ctx.Server!.EnvironmentName, Result: "Denied", Detail: string.Join(" ", blocking.Select(v => v.Message))));
            return OpResult<Guid>.Fail(string.Join(" ", blocking.Select(v => v.Message)));
        }
        var exceptionable = ctx.Violations!.Where(v => v.Exceptionable).ToList();
        LinkException? ex = null;
        if (exceptionable.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(exceptionReason))
                return OpResult<Guid>.Fail(string.Join(" ", exceptionable.Select(v => v.Message)) + " A Platform Admin can allow this by giving a reason.");
            ex = new LinkException { Id = Guid.NewGuid(), Reason = exceptionReason.Trim(), Violation = string.Join(" ", exceptionable.Select(v => v.Message)), GrantedBy = by, GrantedAt = clock.GetUtcNow() };
            db.LinkExceptions.Add(ex);
        }

        var env = ctx.Server!.EnvironmentName!;
        var order = (await db.DeploymentTargets.Where(t => t.ComponentId == componentId && t.EnvironmentName == env).Select(t => (int?)t.DeployOrder).MaxAsync() ?? 0) + 1;
        var now = clock.GetUtcNow();
        var t = new DeploymentTarget
        {
            Id = Guid.NewGuid(), ComponentId = componentId, EnvironmentName = env, ServerId = serverId, IisSiteId = iisSiteId, IisSiteName = ctx.Site!.Name,
            AppPath = ctx.Candidate!.AppPath, PhysicalPath = ctx.App!.PhysicalPath ?? "", AppPoolName = ctx.App.AppPoolName ?? "",
            DeployOrder = order, Status = TargetStatus.Active, LastVerifiedAt = now, LinkExceptionId = ex?.Id, CreatedAt = now, CreatedBy = by,
        };
        db.DeploymentTargets.Add(t);
        var siteRow = await db.IisSites.FirstAsync(s => s.ServerId == serverId && s.IisSiteId == iisSiteId);
        siteRow.MappingStatus = MappingStatus.Mapped;
        await db.SaveChangesAsync();

        await audit.WriteAsync(actorForAudit, new AuditRecord(action, "DeploymentTarget", t.Id.ToString(), serverId, env,
            After: $"{ctx.Project!.Name} / {ctx.Component!.Name} ({env}) <- {ctx.Server.Name}: site '{t.IisSiteName}' (id {iisSiteId}) app '{t.AppPath}'; folder={t.PhysicalPath}; pool={t.AppPoolName}; order={order}"
                   + (ex is null ? "" : $"; EXCEPTION: {ex.Reason}")));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        return OpResult<Guid>.Success(t.Id);
    }

    public async Task<OpResult> RemoveAsync(Actor actor, Guid targetId)
    {
        await RequireAdminAsync(actor, "Target.Remove", targetId.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.DeploymentTargets.FirstOrDefaultAsync(x => x.Id == targetId);
        if (t is null) return OpResult.Fail("Target not found.");
        db.DeploymentTargets.Remove(t);
        foreach (var d in await db.DriftEvents.Where(x => x.TargetId == targetId).ToListAsync()) d.TargetId = null;
        await db.SaveChangesAsync();
        if (!await db.DeploymentTargets.AnyAsync(x => x.ServerId == t.ServerId && x.IisSiteId == t.IisSiteId))
        {
            var site = await db.IisSites.FirstOrDefaultAsync(s => s.ServerId == t.ServerId && s.IisSiteId == t.IisSiteId);
            if (site is not null) { site.MappingStatus = MappingStatus.Unassigned; await db.SaveChangesAsync(); }
        }
        await audit.WriteAsync(actor, new AuditRecord("Target.Remove", "DeploymentTarget", targetId.ToString(), t.ServerId, t.EnvironmentName,
            Before: $"site '{t.IisSiteName}' app '{t.AppPath}' folder={t.PhysicalPath}"));
        bus.Publish(new ServerChanged(t.ServerId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    public async Task<OpResult> SetDisabledAsync(Actor actor, Guid targetId, bool disabled)
    {
        await RequireAdminAsync(actor, "Target.SetDisabled", targetId.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.DeploymentTargets.FirstOrDefaultAsync(x => x.Id == targetId);
        if (t is null) return OpResult.Fail("Target not found.");
        var before = t.Status;
        t.Status = disabled ? TargetStatus.Disabled : TargetStatus.Active;
        t.StatusDetail = disabled ? "Disabled by an admin." : null;
        await db.SaveChangesAsync();
        if (!disabled) await reconciler.VerifyServerAsync(t.ServerId);   // comes back as Active, Missing or Drifted based on IIS
        await audit.WriteAsync(actor, new AuditRecord("Target.SetDisabled", "DeploymentTarget", targetId.ToString(), t.ServerId, t.EnvironmentName, Before: before.ToString(), After: disabled ? "Disabled" : "Enabled"));
        bus.Publish(new ServerChanged(t.ServerId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    /// <summary>Accepts what IIS says now (new folder, new pool, or a rename). Re-checks every link rule first.</summary>
    public async Task<OpResult> AcceptCurrentAsync(Actor actor, Guid targetId)
    {
        await RequireAdminAsync(actor, "Target.AcceptDrift", targetId.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.DeploymentTargets.FirstOrDefaultAsync(x => x.Id == targetId);
        if (t is null) return OpResult.Fail("Target not found.");
        if (t.Status is TargetStatus.Active or TargetStatus.Disabled) return OpResult.Fail("Nothing to accept: the target is not drifted or missing.");

        var ctx = await LoadContextAsync(db, t.ComponentId, t.ServerId, t.IisSiteId, t.AppPath, t.Id);
        if (ctx.Error is not null) return OpResult.Fail("The item no longer exists in IIS, so there is nothing to accept. Remove the target and link again.");
        var blocking = ctx.Violations!.Where(v => !v.Exceptionable && v.Code is not LinkViolationCode.SiteAlreadyLinked).ToList();
        if (blocking.Count > 0) return OpResult.Fail("Cannot accept: " + string.Join(" ", blocking.Select(v => v.Message)));
        if (ctx.Violations!.Any(v => v.Exceptionable) && t.LinkExceptionId is null)
            return OpResult.Fail("Cannot accept: " + string.Join(" ", ctx.Violations!.Where(v => v.Exceptionable).Select(v => v.Message)) + " Remove the target and link it again with a recorded exception.");

        var before = $"site '{t.IisSiteName}'; folder={t.PhysicalPath}; pool={t.AppPoolName}; status={t.Status}";
        t.IisSiteName = ctx.Site!.Name; t.PhysicalPath = ctx.App!.PhysicalPath ?? ""; t.AppPoolName = ctx.App.AppPoolName ?? "";
        t.Status = TargetStatus.Active; t.StatusDetail = null; t.LastVerifiedAt = clock.GetUtcNow();
        foreach (var d in await db.DriftEvents.Where(x => x.TargetId == t.Id && x.AcknowledgedAt == null).ToListAsync())
        { d.AcknowledgedBy = actor.UserName; d.AcknowledgedAt = clock.GetUtcNow(); }
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Target.AcceptDrift", "DeploymentTarget", t.Id.ToString(), t.ServerId, t.EnvironmentName,
            Before: before, After: $"site '{t.IisSiteName}'; folder={t.PhysicalPath}; pool={t.AppPoolName}"));
        bus.Publish(new ServerChanged(t.ServerId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    public async Task<OpResult> MoveAsync(Actor actor, Guid targetId, bool up)
    {
        await RequireAdminAsync(actor, "Target.Reorder", targetId.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.DeploymentTargets.FirstOrDefaultAsync(x => x.Id == targetId);
        if (t is null) return OpResult.Fail("Target not found.");
        var group = await db.DeploymentTargets.Where(x => x.ComponentId == t.ComponentId && x.EnvironmentName == t.EnvironmentName).OrderBy(x => x.DeployOrder).ThenBy(x => x.CreatedAt).ToListAsync();
        var i = group.FindIndex(x => x.Id == t.Id);
        var j = up ? i - 1 : i + 1;
        if (j < 0 || j >= group.Count) return OpResult.Fail(up ? "Already first." : "Already last.");
        (group[i], group[j]) = (group[j], group[i]);
        for (var k = 0; k < group.Count; k++) group[k].DeployOrder = k + 1;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Target.Reorder", "DeploymentTarget", targetId.ToString(), t.ServerId, t.EnvironmentName,
            After: string.Join(" > ", group.Select(x => x.Id.ToString("N")[..6]))));
        return OpResult.Success();
    }

    // ---------- link requests (Project Owner asks, Platform Admin decides) ----------

    public async Task<OpResult<Guid>> RequestLinkAsync(Actor actor, Guid componentId, Guid serverId, long iisSiteId, string appPath, string reason)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var component = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId);
        if (component is null) return OpResult<Guid>.Fail("Component not found.");
        if (!actor.Access.CanOnProject(ProjectPermission.RequestLink, component.ProjectId))
        {
            await audit.WriteAsync(actor, new AuditRecord("LinkRequest.Create", "Component", componentId.ToString(), Result: "Denied"));
            throw new ForbiddenException("Only a Project Owner or Platform Admin can request a link for this project.");
        }
        if (string.IsNullOrWhiteSpace(reason)) return OpResult<Guid>.Fail("Write a short reason for the request.");

        var ctx = await LoadContextAsync(db, componentId, serverId, iisSiteId, appPath, null);
        if (ctx.Error is not null) return OpResult<Guid>.Fail(ctx.Error);
        var blocking = ctx.Violations!.Where(v => !v.Exceptionable).ToList();
        if (blocking.Count > 0) return OpResult<Guid>.Fail(string.Join(" ", blocking.Select(v => v.Message)));
        var np = LinkRules.NormalizeAppPath(appPath);
        if ((await db.LinkRequests.Where(r => r.ServerId == serverId && r.IisSiteId == iisSiteId && r.Status == LinkRequestStatus.Pending).ToListAsync())
            .Any(r => LinkRules.NormalizeAppPath(r.AppPath) == np))
            return OpResult<Guid>.Fail("There is already a pending request for this item.");

        var req = new LinkRequest
        {
            Id = Guid.NewGuid(), ComponentId = componentId, EnvironmentName = ctx.Server!.EnvironmentName!, ServerId = serverId, IisSiteId = iisSiteId, AppPath = np,
            Reason = reason.Trim(), RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedAt = clock.GetUtcNow(),
        };
        db.LinkRequests.Add(req);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("LinkRequest.Create", "LinkRequest", req.Id.ToString(), serverId, req.EnvironmentName,
            After: $"{ctx.Project!.Name} / {ctx.Component!.Name} <- {ctx.Server.Name}: '{ctx.Site!.Name}' {np}; reason: {req.Reason}"));
        return OpResult<Guid>.Success(req.Id);
    }

    public async Task<IReadOnlyList<LinkRequestRow>> ListRequestsAsync(Actor actor, bool pendingOnly)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = from r in db.LinkRequests.AsNoTracking()
                join c in db.Components.AsNoTracking() on r.ComponentId equals c.Id
                join p in db.Projects.AsNoTracking() on c.ProjectId equals p.Id
                join s in db.Servers.IgnoreQueryFilters().AsNoTracking() on r.ServerId equals s.Id
                where !pendingOnly || r.Status == LinkRequestStatus.Pending
                select new { r, ProjectId = p.Id, Project = p.Name, Component = c.Name, Server = s.Name };
        var rows = await q.ToListAsync();
        var siteNames = await db.IisSites.AsNoTracking().Select(s => new { s.ServerId, s.IisSiteId, s.Name }).ToListAsync();
        var admin = IsAdmin(actor);
        return rows.Where(x => admin || actor.Access.CanOnProject(ProjectPermission.RequestLink, x.ProjectId))
            .OrderByDescending(x => x.r.RequestedAt)
            .Select(x => new LinkRequestRow(x.r.Id, x.Project, x.Component, x.r.EnvironmentName, x.Server,
                siteNames.FirstOrDefault(s => s.ServerId == x.r.ServerId && s.IisSiteId == x.r.IisSiteId)?.Name ?? $"site {x.r.IisSiteId}", x.r.AppPath, x.r.Reason,
                x.r.Status, x.r.RequestedBy, x.r.RequestedAt, x.r.DecidedBy, x.r.DecisionComment)).ToList();
    }

    public async Task<OpResult> DecideAsync(Actor actor, Guid requestId, bool approve, string? comment, string? exceptionReason = null)
    {
        await RequireAdminAsync(actor, "LinkRequest.Decide", requestId.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var r = await db.LinkRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != LinkRequestStatus.Pending) return OpResult.Fail("Request not found or already decided.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) return OpResult.Fail("A comment is required when rejecting.");

        if (approve)
        {
            var linked = await LinkCoreAsync(actor.UserName, actor, r.ComponentId, r.ServerId, r.IisSiteId, r.AppPath, exceptionReason, "Target.Link");
            if (!linked.Ok) return OpResult.Fail(linked.Error!);
        }
        r.Status = approve ? LinkRequestStatus.Approved : LinkRequestStatus.Rejected;
        r.DecidedBy = actor.UserName; r.DecidedAt = clock.GetUtcNow(); r.DecisionComment = comment?.Trim();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("LinkRequest.Decide", "LinkRequest", requestId.ToString(), r.ServerId, r.EnvironmentName,
            After: $"{(approve ? "Approved" : "Rejected")}; requested by {r.RequestedBy}; {comment}"));
        return OpResult.Success();
    }

    public async Task<OpResult> CancelRequestAsync(Actor actor, Guid requestId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var r = await db.LinkRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != LinkRequestStatus.Pending) return OpResult.Fail("Request not found or already decided.");
        if (!string.Equals(r.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase) && !IsAdmin(actor)) throw new ForbiddenException("Only the requester can cancel it.");
        r.Status = LinkRequestStatus.Cancelled; r.DecidedBy = actor.UserName; r.DecidedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("LinkRequest.Cancel", "LinkRequest", requestId.ToString(), r.ServerId, r.EnvironmentName));
        return OpResult.Success();
    }

    private async Task RequireAdminAsync(Actor actor, string action, string target)
    {
        if (IsAdmin(actor)) return;
        await audit.WriteAsync(actor, new AuditRecord(action, "DeploymentTarget", target, Result: "Denied"));
        throw new ForbiddenException("Only a Platform Admin can do this.");
    }
}
