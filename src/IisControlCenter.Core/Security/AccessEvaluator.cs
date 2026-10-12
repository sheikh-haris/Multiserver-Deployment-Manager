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
}
