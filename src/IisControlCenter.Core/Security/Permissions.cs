namespace IisControlCenter.Core.Security;

using IisControlCenter.Core.Domain;

/// <summary>Platform permissions. Project permissions arrive in Phase 2 and are checked separately on purpose.</summary>
public enum Permission
{
    ViewServers,
    ManageServers,      // add, approve, reject, revoke, remove, change mode
    TriggerDiscovery,
    ViewDiscoveryReport,
    ViewAudit,
    ManageRoles,
    ManageSettings,
    ManageIis,          // Phase 4
}

public static class RolePermissions
{
    private static readonly Dictionary<Role, Permission[]> Map = new()
    {
        [Role.PlatformAdmin] = Enum.GetValues<Permission>(),
        [Role.ServerOperator] = new[]
        {
            Permission.ViewServers, Permission.TriggerDiscovery,
            Permission.ViewDiscoveryReport, Permission.ManageIis,
        },
        [Role.Auditor] = new[]
        {
            Permission.ViewServers, Permission.ViewDiscoveryReport, Permission.ViewAudit,
        },
    };

    public static IReadOnlyList<Role> PlatformRoles { get; } =
        new[] { Role.PlatformAdmin, Role.ServerOperator, Role.Auditor };

    public static bool Grants(Role role, Permission permission) =>
        Map.TryGetValue(role, out var perms) && perms.Contains(permission);

    /// <summary>Only these permissions are limited by environment. Everything else is global.</summary>
    public static bool IsEnvironmentScoped(Role role) => role == Role.ServerOperator;
}
