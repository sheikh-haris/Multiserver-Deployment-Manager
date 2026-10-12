using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record MemberRow(Guid Id, PrincipalType PrincipalType, string PrincipalId, string PrincipalName, Role Role,
    string? EnvironmentName, DateTimeOffset? ExpiresAt, bool Expired, string GrantedBy, DateTimeOffset GrantedAt, bool CanRevoke);

public sealed record AccessRow(string Scope, string? ProjectName, Role Role, string? EnvironmentName, string Source, DateTimeOffset? ExpiresAt);

public sealed class MembershipService(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<MemberRow>> ListAsync(Actor actor, Guid projectId)
    {
        if (!actor.Access.CanOnProject(ProjectPermission.View, projectId)) throw new ForbiddenException("You do not have access to this project.");
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.Scope == RoleScope.Project && r.ProjectId == projectId && r.RevokedAt == null)
            .OrderBy(r => r.PrincipalName).ToListAsync();
        var now = clock.GetUtcNow();
        var admin = actor.Can(Permission.ManageProjects);
        var owner = actor.Access.IsProjectOwner(projectId);
        var visible = ProjectService.VisibleEnvironmentsFor(actor, projectId);
        return rows
            .Where(r => visible is null || r.EnvironmentName is null || visible.Contains(r.EnvironmentName))
            .Select(r => new MemberRow(r.Id, r.PrincipalType, r.PrincipalId, r.PrincipalName, r.Role, r.EnvironmentName, r.ExpiresAt,
                r.ExpiresAt is not null && r.ExpiresAt <= now, r.GrantedBy, r.GrantedAt, GrantRules.Check(admin, owner, r.Role) is null)).ToList();
    }

    public async Task<OpResult> GrantAsync(Actor actor, Guid projectId, PrincipalType type, string principalId, string principalName,
        Role role, string? environment, DateTimeOffset? expiresAt)
    {
        var admin = actor.Can(Permission.ManageProjects);
        var owner = actor.Access.IsProjectOwner(projectId);
        if (!admin && !owner) { await DeniedAsync(actor, "ProjectRole.Grant", projectId, role, environment); throw new ForbiddenException("You cannot manage members of this project."); }

        var rule = GrantRules.Check(admin, owner, role);
        if (rule is not null)
        {
            await DeniedAsync(actor, "ProjectRole.Grant", projectId, role, environment, rule);
            return OpResult.Fail(rule);
        }

        principalId = (principalId ?? "").Trim();
        if (principalId.Length == 0) return OpResult.Fail("Enter the group or user object id.");
        if (type == PrincipalType.User && expiresAt is null) return OpResult.Fail("Individual users need an expiry date. Use a group for permanent access.");
        if (expiresAt is not null && expiresAt <= clock.GetUtcNow()) return OpResult.Fail("Expiry must be in the future.");

        var scoped = ProjectRolePermissions.IsEnvironmentScoped(role);
        if (scoped && string.IsNullOrEmpty(environment)) return OpResult.Fail("Choose an environment.");
        if (!scoped) environment = null;

        await using var db = await dbf.CreateDbContextAsync();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
        if (project is null) return OpResult.Fail("Project not found.");
        if (environment is not null && !await db.Environments.AnyAsync(e => e.Name == environment)) return OpResult.Fail("Unknown environment.");

        var existing = await db.RoleAssignments.Where(r => r.Scope == RoleScope.Project && r.ProjectId == projectId && r.RevokedAt == null
            && r.PrincipalId == principalId && r.Role == role && r.EnvironmentName == environment).ToListAsync();
        if (existing.Any(r => r.ExpiresAt is null || r.ExpiresAt > clock.GetUtcNow())) return OpResult.Fail("This person or group already has that role there.");

        var a = new RoleAssignment
        {
            Id = Guid.NewGuid(), PrincipalType = type, PrincipalId = principalId, PrincipalName = string.IsNullOrWhiteSpace(principalName) ? principalId : principalName.Trim(),
            Scope = RoleScope.Project, ProjectId = projectId, Role = role, EnvironmentName = environment, GrantedBy = actor.UserName,
            GrantedAt = clock.GetUtcNow(), ExpiresAt = expiresAt,
        };
        db.RoleAssignments.Add(a);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("ProjectRole.Grant", "RoleAssignment", a.Id.ToString(), EnvironmentName: environment,
            After: $"project={project.Name}; {type} {a.PrincipalName} ({principalId}) -> {role}; env={environment ?? "all"}; expires={expiresAt:O}"));
        return OpResult.Success();
    }

    public async Task<OpResult> RevokeAsync(Actor actor, Guid assignmentId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var a = await db.RoleAssignments.FirstOrDefaultAsync(r => r.Id == assignmentId && r.Scope == RoleScope.Project && r.RevokedAt == null);
        if (a is null || a.ProjectId is null) return OpResult.Fail("Assignment not found.");

        var admin = actor.Can(Permission.ManageProjects);
        var owner = actor.Access.IsProjectOwner(a.ProjectId.Value);
        var rule = GrantRules.Check(admin, owner, a.Role);
        if (rule is not null)
        {
            await DeniedAsync(actor, "ProjectRole.Revoke", a.ProjectId.Value, a.Role, a.EnvironmentName, rule);
            if (!admin && !owner) throw new ForbiddenException("You cannot manage members of this project.");
            return OpResult.Fail(rule);
        }
        a.RevokedAt = clock.GetUtcNow(); a.RevokedBy = actor.UserName;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("ProjectRole.Revoke", "RoleAssignment", a.Id.ToString(), EnvironmentName: a.EnvironmentName,
            Before: $"{a.PrincipalType} {a.PrincipalName} ({a.PrincipalId}) -> {a.Role}; env={a.EnvironmentName ?? "all"}"));
        return OpResult.Success();
    }

    /// <summary>Everything the signed-in user holds, and why (direct or through a group).</summary>
    public async Task<IReadOnlyList<AccessRow>> MyAccessAsync(Actor actor)
    {
        var assignments = actor.Access.ActiveAssignments;
        var pids = assignments.Where(a => a.ProjectId is not null).Select(a => a.ProjectId!.Value).Distinct().ToList();
        Dictionary<Guid, string> names;
        await using (var db = await dbf.CreateDbContextAsync())
            names = await db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return assignments
            .Select(a => new AccessRow(a.Scope.ToString(), a.ProjectId is { } id ? names.GetValueOrDefault(id) : null, a.Role, a.EnvironmentName,
                a.GrantedBy == "bootstrap" ? "Bootstrap admin (configuration)" : a.PrincipalType == PrincipalType.Group ? $"Group: {a.PrincipalName}" : "Directly assigned", a.ExpiresAt))
            .OrderBy(r => r.Scope).ThenBy(r => r.ProjectName).ThenBy(r => r.Role).ToList();
    }

    private Task DeniedAsync(Actor actor, string action, Guid projectId, Role role, string? env, string? why = null) =>
        audit.WriteAsync(actor, new AuditRecord(action, "Project", projectId.ToString(), EnvironmentName: env, Result: "Denied", Detail: $"role={role}; {why}"));
}
