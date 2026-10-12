using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Security;

public sealed record UserContext(string UserId, string DisplayName, IReadOnlyCollection<string> GroupIds);

/// <summary>
/// One place for platform permission decisions. Deny by default.
/// Project roles can never satisfy a platform permission, and the reverse.
/// </summary>
public sealed class AccessEvaluator
{
    private readonly IReadOnlyList<RoleAssignment> _assignments;
    private readonly UserContext _user;
    private readonly DateTimeOffset _now;

    public AccessEvaluator(UserContext user, IEnumerable<RoleAssignment> assignments, DateTimeOffset now)
    {
        _user = user;
        _now = now;
        _assignments = assignments.Where(a => IsActive(a, now) && Matches(a, user)).ToList();
    }

    public static bool IsActive(RoleAssignment a, DateTimeOffset now) =>
        a.RevokedAt is null && (a.ExpiresAt is null || a.ExpiresAt > now);

    private static bool Matches(RoleAssignment a, UserContext user) => a.PrincipalType switch
    {
        PrincipalType.User => string.Equals(a.PrincipalId, user.UserId, StringComparison.OrdinalIgnoreCase),
        PrincipalType.Group => user.GroupIds.Contains(a.PrincipalId, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    /// <param name="environment">Environment of the thing being acted on. Null = environment-independent action.</param>
    public bool Can(Permission permission, string? environment = null)
    {
        foreach (var a in _assignments)
        {
            if (a.Scope != RoleScope.Platform) continue;
            if (!RolePermissions.Grants(a.Role, permission)) continue;

            if (RolePermissions.IsEnvironmentScoped(a.Role) && environment is not null && a.EnvironmentName is not null)
            {
                if (!string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase)) continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>Environments the user may act on for this permission. Null means all.</summary>
    public IReadOnlySet<string>? AllowedEnvironments(Permission permission)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _assignments)
        {
            if (a.Scope != RoleScope.Platform || !RolePermissions.Grants(a.Role, permission)) continue;
            if (!RolePermissions.IsEnvironmentScoped(a.Role) || a.EnvironmentName is null) return null;
            set.Add(a.EnvironmentName);
        }
        return set;
    }

    public bool IsAnyRole => _assignments.Count > 0;

    /// <summary>
    /// Project permission check. Needs a project role for that project and environment.
    /// Platform Admin may also View and manage members and requests links on any project (to run the platform),
    /// but never gets Submit or Approve from the platform role: deploying needs the project role.
    /// </summary>
    public bool CanOnProject(ProjectPermission permission, Guid projectId, string? environment = null)
    {
        if (permission is ProjectPermission.View or ProjectPermission.ManageMembers or ProjectPermission.RequestLink
            && Can(Permission.ManageProjects)) return true;
        if (permission == ProjectPermission.View && Can(Permission.ViewAllProjects)) return true;

        foreach (var a in _assignments)
        {
            if (a.Scope != RoleScope.Project || a.ProjectId != projectId) continue;
            if (!ProjectRolePermissions.Grants(a.Role, permission)) continue;
            if (ProjectRolePermissions.IsEnvironmentScoped(a.Role) && environment is not null)
            {
                if (!string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (ProjectRolePermissions.IsEnvironmentScoped(a.Role) && permission != ProjectPermission.View)
            {
                // An environment-scoped action needs an environment.
                continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>The assignments (not the platform override) that currently allow this project permission in this environment.</summary>
    public IReadOnlyList<RoleAssignment> AssignmentsFor(ProjectPermission permission, Guid projectId, string? environment)
    {
        var list = new List<RoleAssignment>();
        foreach (var a in _assignments)
        {
            if (a.Scope != RoleScope.Project || a.ProjectId != projectId || !ProjectRolePermissions.Grants(a.Role, permission)) continue;
            if (ProjectRolePermissions.IsEnvironmentScoped(a.Role) && !string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(a);
        }
        return list;
    }

    /// <summary>Environments in which the user holds a role that grants this permission on the project.</summary>
    public IReadOnlySet<string> EnvironmentsFor(ProjectPermission permission, Guid projectId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _assignments)
            if (a.Scope == RoleScope.Project && a.ProjectId == projectId && a.EnvironmentName is not null
                && ProjectRolePermissions.Grants(a.Role, permission))
                set.Add(a.EnvironmentName);
        return set;
    }

    /// <summary>Projects where the user has any project role. Platform-wide viewers are handled by the caller.</summary>
    public IReadOnlySet<Guid> ProjectIdsWithAnyRole() =>
        _assignments.Where(a => a.Scope == RoleScope.Project && a.ProjectId is not null).Select(a => a.ProjectId!.Value).ToHashSet();

    public bool IsProjectOwner(Guid projectId) =>
        _assignments.Any(a => a.Scope == RoleScope.Project && a.ProjectId == projectId && a.Role == Role.ProjectOwner);

    public IReadOnlyList<RoleAssignment> ActiveAssignments => _assignments;
}
