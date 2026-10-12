using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Security;

/// <summary>Project permissions are separate from platform permissions on purpose. Neither satisfies the other.</summary>
public enum ProjectPermission
{
    View,                // components, targets, history
    SubmitDeployment,    // Phase 3
    ApproveDeployment,   // Phase 3
    ManageMembers,       // grant roles up to Deployer
    RequestLink,         // ask for a component link
}

public static class ProjectRolePermissions
{
    private static readonly Dictionary<Role, ProjectPermission[]> Map = new()
    {
        [Role.ProjectViewer] = [ProjectPermission.View],
        [Role.Deployer] = [ProjectPermission.View, ProjectPermission.SubmitDeployment],
        [Role.Approver] = [ProjectPermission.View, ProjectPermission.ApproveDeployment],
        [Role.ProjectOwner] = [ProjectPermission.View, ProjectPermission.ManageMembers, ProjectPermission.RequestLink],
    };

    public static IReadOnlyList<Role> ProjectRoles { get; } = [Role.ProjectViewer, Role.Deployer, Role.Approver, Role.ProjectOwner];

    public static bool Grants(Role role, ProjectPermission p) => Map.TryGetValue(role, out var l) && l.Contains(p);

    /// <summary>Owner is project-wide. The other roles are given per environment.</summary>
    public static bool IsEnvironmentScoped(Role role) => role != Role.ProjectOwner;
}

/// <summary>Who may grant which project role (decision D4 and the brief).</summary>
public static class GrantRules
{
    /// <summary>Returns null when allowed, otherwise the reason.</summary>
    public static string? Check(bool grantorIsPlatformAdmin, bool grantorIsProjectOwner, Role role)
    {
        if (!ProjectRolePermissions.ProjectRoles.Contains(role)) return "Not a project role.";
        if (grantorIsPlatformAdmin) return null;
        if (!grantorIsProjectOwner) return "You cannot manage members of this project.";
        return role switch
        {
            Role.ProjectViewer or Role.Deployer => null,
            Role.Approver => "Only a Platform Admin can grant the Approver role.",
            Role.ProjectOwner => "Only a Platform Admin can grant the Project Owner role.",
            _ => "Not allowed.",
        };
    }
}
