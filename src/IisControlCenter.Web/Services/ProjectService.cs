using System.Text.RegularExpressions;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record ProjectRow(Guid Id, string Name, string? Description, string? OwnerGroupName, bool IsActive,
    int ComponentCount, int TargetCount, IReadOnlyList<string> Environments);

public sealed record TargetView(Guid Id, Guid ComponentId, string EnvironmentName, Guid ServerId, string ServerName, string IisSiteName,
    long IisSiteId, string AppPath, string PhysicalPath, string AppPoolName, int DeployOrder, TargetStatus Status, string? StatusDetail,
    DateTimeOffset? LastVerifiedAt, bool HasException);

public sealed record ComponentView(Guid Id, string Name, string Type, string HealthCheckPath, string? AllowedFileTypes,
    bool RequiresStagingBeforeProd, IReadOnlyList<TargetView> Targets, IReadOnlyList<ProtectedPath> ProtectedPaths);

public sealed record ProjectView(Project Project, IReadOnlyList<ComponentView> Components,
    IReadOnlyList<string> VisibleEnvironments, IReadOnlyDictionary<string, bool> SelfApproval,
    bool CanManage, bool CanManageMembers, bool CanRequestLink);

public sealed class ProjectService(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    public static readonly (string Path, ProtectedPathKind Kind)[] DefaultProtectedPaths =
    [
        ("web.config", ProtectedPathKind.ConfigFile),
        ("appsettings*.json", ProtectedPathKind.ConfigFile),
        ("uploads\\", ProtectedPathKind.DataFolder),
        ("generated-files\\", ProtectedPathKind.DataFolder),
    ];

    public static string Slugify(string s)
    {
        var slug = Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "item" : slug.Length > 100 ? slug[..100] : slug;
    }

    // ---------- visibility ----------

    private static bool SeesAll(Actor a) => a.Can(Permission.ViewAllProjects) || a.Can(Permission.ManageProjects);

    /// <summary>Null = every environment. Otherwise only these.</summary>
    public static IReadOnlySet<string>? VisibleEnvironmentsFor(Actor a, Guid projectId)
    {
        if (SeesAll(a) || a.Access.IsProjectOwner(projectId)) return null;
        return a.Access.EnvironmentsFor(ProjectPermission.View, projectId);
    }

    public async Task<IReadOnlyList<ProjectRow>> ListAsync(Actor actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Projects.AsNoTracking();
        if (!SeesAll(actor))
        {
            var ids = actor.Access.ProjectIdsWithAnyRole().ToList();
            q = q.Where(p => ids.Contains(p.Id));
        }
        var projects = await q.OrderBy(p => p.Name).ToListAsync();
        var pids = projects.Select(p => p.Id).ToList();
        var comps = await db.Components.AsNoTracking().Where(c => pids.Contains(c.ProjectId)).Select(c => new { c.Id, c.ProjectId }).ToListAsync();
        var cids = comps.Select(c => c.Id).ToList();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => cids.Contains(t.ComponentId)).Select(t => new { t.ComponentId, t.EnvironmentName }).ToListAsync();

        var rows = new List<ProjectRow>();
        foreach (var p in projects)
        {
            var visible = VisibleEnvironmentsFor(actor, p.Id);
            var pc = comps.Where(c => c.ProjectId == p.Id).Select(c => c.Id).ToHashSet();
            var pt = targets.Where(t => pc.Contains(t.ComponentId) && (visible is null || visible.Contains(t.EnvironmentName))).ToList();
            rows.Add(new ProjectRow(p.Id, p.Name, p.Description, p.OwnerGroupName, p.IsActive, pc.Count, pt.Count,
                pt.Select(t => t.EnvironmentName).Distinct().OrderBy(x => x).ToList()));
        }
        return rows;
    }

    public sealed record ComponentOption(Guid ProjectId, string ProjectName, Guid ComponentId, string ComponentName);

    /// <summary>Components the user may map IIS items to (or request a link for): all for a Platform Admin, own projects for an owner.</summary>
    public async Task<IReadOnlyList<ComponentOption>> ListComponentOptionsAsync(Actor actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await (from c in db.Components.AsNoTracking() join p in db.Projects.AsNoTracking() on c.ProjectId equals p.Id where p.IsActive
                          select new ComponentOption(p.Id, p.Name, c.Id, c.Name)).ToListAsync();
        return rows.Where(r => actor.Access.CanOnProject(ProjectPermission.RequestLink, r.ProjectId)).OrderBy(r => r.ProjectName).ThenBy(r => r.ComponentName).ToList();
    }

    public async Task<ProjectView?> GetAsync(Actor actor, Guid projectId)
    {
        if (!actor.Access.CanOnProject(ProjectPermission.View, projectId)) return null;
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return null;

        var visible = VisibleEnvironmentsFor(actor, projectId);
        var comps = await db.Components.AsNoTracking().Where(c => c.ProjectId == projectId).Include(c => c.ProtectedPaths).OrderBy(c => c.Name).ToListAsync();
        var cids = comps.Select(c => c.Id).ToList();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => cids.Contains(t.ComponentId)).ToListAsync();
        var serverNames = await db.Servers.AsNoTracking().Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name);

        var views = comps.Select(c => new ComponentView(c.Id, c.Name, c.Type, c.HealthCheckPath, c.AllowedFileTypes, c.RequiresStagingBeforeProd,
            targets.Where(t => t.ComponentId == c.Id && (visible is null || visible.Contains(t.EnvironmentName)))
                .OrderBy(t => t.EnvironmentName).ThenBy(t => t.DeployOrder)
                .Select(t => new TargetView(t.Id, t.ComponentId, t.EnvironmentName, t.ServerId, serverNames.GetValueOrDefault(t.ServerId, "?"),
                    t.IisSiteName, t.IisSiteId, t.AppPath, t.PhysicalPath, t.AppPoolName, t.DeployOrder, t.Status, t.StatusDetail, t.LastVerifiedAt, t.LinkExceptionId != null)).ToList(),
            c.ProtectedPaths.OrderBy(x => x.Kind).ThenBy(x => x.Path).ToList())).ToList();

        var envs = await db.Environments.AsNoTracking().OrderBy(e => e.SortOrder).Select(e => e.Name).ToListAsync();
        var settings = await db.ProjectEnvironmentSettings.AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync();
        var self = envs.ToDictionary(e => e, e => settings.FirstOrDefault(s => s.EnvironmentName == e)?.AllowSelfApproval ?? false);

        return new ProjectView(p, views, visible is null ? envs : envs.Where(visible.Contains).ToList(), self,
            actor.Can(Permission.ManageProjects),
            actor.Access.CanOnProject(ProjectPermission.ManageMembers, projectId),
            actor.Access.CanOnProject(ProjectPermission.RequestLink, projectId));
    }

    // ---------- projects ----------

    public async Task<OpResult<Guid>> CreateAsync(Actor actor, string name, string? description, string? ownerGroupId, string? ownerGroupName)
    {
        await RequireAsync(actor, "Project.Create", null);
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 200) return OpResult<Guid>.Fail("Name must be 2 to 200 characters.");
        await using var db = await dbf.CreateDbContextAsync();
        var slug = Slugify(name);
        if (await db.Projects.AnyAsync(p => p.Name == name || p.Slug == slug)) return OpResult<Guid>.Fail("A project with this name already exists.");

        var now = clock.GetUtcNow();
        var p = new Project
        {
            Id = Guid.NewGuid(), Name = name, Slug = slug, Description = description?.Trim(), IsActive = true, ProdApproversRequired = 1,
            OwnerGroupId = string.IsNullOrWhiteSpace(ownerGroupId) ? null : ownerGroupId.Trim(),
            OwnerGroupName = string.IsNullOrWhiteSpace(ownerGroupName) ? null : ownerGroupName.Trim(),
            CreatedAt = now, CreatedBy = actor.UserName,
        };
        db.Projects.Add(p);
        if (p.OwnerGroupId is not null)
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(), PrincipalType = PrincipalType.Group, PrincipalId = p.OwnerGroupId, PrincipalName = p.OwnerGroupName ?? p.OwnerGroupId,
                Scope = RoleScope.Project, ProjectId = p.Id, Role = Role.ProjectOwner, GrantedBy = actor.UserName, GrantedAt = now,
            });
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.Create", "Project", p.Id.ToString(), After: $"{name}; owner group={p.OwnerGroupName ?? "none"}"));
        return OpResult<Guid>.Success(p.Id);
    }

    public async Task<OpResult> UpdateAsync(Actor actor, Guid projectId, string name, string? description)
    {
        await RequireAsync(actor, "Project.Update", projectId);
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 200) return OpResult.Fail("Name must be 2 to 200 characters.");
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return OpResult.Fail("Project not found.");
        var slug = Slugify(name);
        if (await db.Projects.AnyAsync(x => x.Id != projectId && (x.Name == name || x.Slug == slug))) return OpResult.Fail("Another project already has this name.");
        var before = $"{p.Name}; {p.Description}";
        p.Name = name; p.Slug = slug; p.Description = description?.Trim();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.Update", "Project", projectId.ToString(), Before: before, After: $"{p.Name}; {p.Description}"));
        return OpResult.Success();
    }

    public async Task<OpResult> SetActiveAsync(Actor actor, Guid projectId, bool active)
    {
        await RequireAsync(actor, "Project.SetActive", projectId);
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return OpResult.Fail("Project not found.");
        var before = p.IsActive;
        p.IsActive = active;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.SetActive", "Project", projectId.ToString(), Before: before.ToString(), After: active.ToString()));
        return OpResult.Success();
    }

    public async Task<OpResult> SetProdApproversAsync(Actor actor, Guid projectId, int count)
    {
        await RequireAsync(actor, "Project.SetProdApprovers", projectId);
        if (count is not (1 or 2)) return OpResult.Fail("Choose 1 or 2 approvers.");
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return OpResult.Fail("Project not found.");
        var before = p.ProdApproversRequired;
        p.ProdApproversRequired = count;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.SetProdApprovers", "Project", projectId.ToString(), EnvironmentName: "Prod", Before: before.ToString(), After: count.ToString()));
        return OpResult.Success();
    }

    public async Task<OpResult> SetNotificationEmailAsync(Actor actor, Guid projectId, string? addresses)
    {
        await RequireAsync(actor, "Project.SetNotificationEmail", projectId);
        addresses = string.IsNullOrWhiteSpace(addresses) ? null : addresses.Trim();
        if (addresses is not null)
        {
            if (addresses.Length > 500) return OpResult.Fail("Too long.");
            foreach (var a in addresses.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!System.Net.Mail.MailAddress.TryCreate(a, out _)) return OpResult.Fail($"'{a}' is not a valid email address.");
        }
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == projectId);
        if (p is null) return OpResult.Fail("Project not found.");
        var before = p.NotificationEmail;
        p.NotificationEmail = addresses;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.SetNotificationEmail", "Project", projectId.ToString(), Before: before, After: addresses));
        return OpResult.Success();
    }

    /// <summary>Decision D5: off by default. Turning it on is audited. Using it later is badged and listed in a report.</summary>
    public async Task<OpResult> SetSelfApprovalAsync(Actor actor, Guid projectId, string environment, bool allow)
    {
        await RequireAsync(actor, "Project.SetSelfApproval", projectId);
        await using var db = await dbf.CreateDbContextAsync();
        if (!await db.Projects.AnyAsync(x => x.Id == projectId)) return OpResult.Fail("Project not found.");
        if (!await db.Environments.AnyAsync(e => e.Name == environment)) return OpResult.Fail("Unknown environment.");
        var s = await db.ProjectEnvironmentSettings.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.EnvironmentName == environment);
        var before = s?.AllowSelfApproval ?? false;
        if (s is null) db.ProjectEnvironmentSettings.Add(s = new ProjectEnvironmentSetting { ProjectId = projectId, EnvironmentName = environment });
        s.AllowSelfApproval = allow; s.UpdatedBy = actor.UserName; s.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Project.SetSelfApproval", "Project", projectId.ToString(), EnvironmentName: environment,
            Before: before.ToString(), After: allow.ToString()));
        return OpResult.Success();
    }

    // ---------- components ----------

    public async Task<OpResult<Guid>> AddComponentAsync(Actor actor, Guid projectId, string name, string type, string? healthCheckPath,
        string? allowedFileTypes, bool requiresStagingBeforeProd)
    {
        await RequireAsync(actor, "Component.Create", projectId);
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 200) return OpResult<Guid>.Fail("Name must be 2 to 200 characters.");
        var health = string.IsNullOrWhiteSpace(healthCheckPath) ? "/" : healthCheckPath.Trim();
        if (!health.StartsWith('/')) return OpResult<Guid>.Fail("Health check path must start with /.");
        var types = ParseFileTypes(allowedFileTypes, out var typeError);
        if (typeError is not null) return OpResult<Guid>.Fail(typeError);

        await using var db = await dbf.CreateDbContextAsync();
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive)) return OpResult<Guid>.Fail("Project not found or inactive.");
        if (await db.Components.AnyAsync(c => c.ProjectId == projectId && c.Name == name)) return OpResult<Guid>.Fail("This project already has a component with that name.");

        var c = new Component
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Name = name, Slug = Slugify(name), Type = string.IsNullOrWhiteSpace(type) ? "Web" : type.Trim(),
            HealthCheckPath = health, AllowedFileTypes = types, RequiresStagingBeforeProd = requiresStagingBeforeProd, CreatedAt = clock.GetUtcNow(),
            ProtectedPaths = DefaultProtectedPaths.Select(d => new ProtectedPath { Id = Guid.NewGuid(), Path = d.Path, Kind = d.Kind, IsDefault = true }).ToList(),
        };
        db.Components.Add(c);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Component.Create", "Component", c.Id.ToString(), After: $"{name}; type={c.Type}; health={health}"));
        return OpResult<Guid>.Success(c.Id);
    }

    public async Task<OpResult> UpdateComponentAsync(Actor actor, Guid componentId, string name, string type, string? healthCheckPath,
        string? allowedFileTypes, bool requiresStagingBeforeProd)
    {
        await RequireAsync(actor, "Component.Update", componentId);
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 200) return OpResult.Fail("Name must be 2 to 200 characters.");
        var health = string.IsNullOrWhiteSpace(healthCheckPath) ? "/" : healthCheckPath.Trim();
        if (!health.StartsWith('/')) return OpResult.Fail("Health check path must start with /.");
        var types = ParseFileTypes(allowedFileTypes, out var typeError);
        if (typeError is not null) return OpResult.Fail(typeError);

        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.FirstOrDefaultAsync(x => x.Id == componentId);
        if (c is null) return OpResult.Fail("Component not found.");
        if (await db.Components.AnyAsync(x => x.Id != componentId && x.ProjectId == c.ProjectId && x.Name == name)) return OpResult.Fail("Another component already has this name.");
        var before = $"{c.Name}; type={c.Type}; health={c.HealthCheckPath}; types={c.AllowedFileTypes}; stagingFirst={c.RequiresStagingBeforeProd}";
        c.Name = name; c.Slug = Slugify(name); c.Type = string.IsNullOrWhiteSpace(type) ? "Web" : type.Trim();
        c.HealthCheckPath = health; c.AllowedFileTypes = types; c.RequiresStagingBeforeProd = requiresStagingBeforeProd;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Component.Update", "Component", componentId.ToString(), Before: before,
            After: $"{c.Name}; type={c.Type}; health={c.HealthCheckPath}; types={c.AllowedFileTypes}; stagingFirst={c.RequiresStagingBeforeProd}"));
        return OpResult.Success();
    }

    public async Task<OpResult> RemoveComponentAsync(Actor actor, Guid componentId)
    {
        await RequireAsync(actor, "Component.Remove", componentId);
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.Include(x => x.ProtectedPaths).FirstOrDefaultAsync(x => x.Id == componentId);
        if (c is null) return OpResult.Fail("Component not found.");
        if (await db.DeploymentTargets.AnyAsync(t => t.ComponentId == componentId)) return OpResult.Fail("Remove the component's deployment targets first.");
        if (await db.LinkRequests.AnyAsync(r => r.ComponentId == componentId && r.Status == LinkRequestStatus.Pending)) return OpResult.Fail("Decide the pending link requests first.");
        db.Components.Remove(c);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Component.Remove", "Component", componentId.ToString(), Before: c.Name));
        return OpResult.Success();
    }

    /// <summary>Normalises ".exe; ps1" into ".exe;.ps1". Only simple extensions are accepted.</summary>
    public static string? ParseFileTypes(string? input, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(input)) return null;
        var list = new List<string>();
        foreach (var raw in input.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var e = raw.StartsWith('.') ? raw : "." + raw;
            if (!Regex.IsMatch(e, @"^\.[a-zA-Z0-9]{1,10}$")) { error = $"'{raw}' is not a valid file extension."; return null; }
            list.Add(e.ToLowerInvariant());
        }
        return string.Join(';', list.Distinct());
    }

    private async Task RequireAsync(Actor actor, string action, object? target)
    {
        if (actor.Can(Permission.ManageProjects)) return;
        await audit.WriteAsync(actor, new AuditRecord(action, "Project", target?.ToString(), Result: "Denied"));
        throw new ForbiddenException("You do not have permission to manage projects.");
    }
}
