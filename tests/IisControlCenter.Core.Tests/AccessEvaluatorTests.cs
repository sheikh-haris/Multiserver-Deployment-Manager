using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;

namespace IisControlCenter.Core.Tests;

public class AccessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly UserContext Alice = new("alice-oid", "Alice", ["group-ops"]);

    private static RoleAssignment Assign(Role role, PrincipalType type = PrincipalType.User, string? principal = null, string? env = null,
        DateTimeOffset? expires = null, DateTimeOffset? revoked = null, RoleScope scope = RoleScope.Platform) => new()
    {
        Id = Guid.NewGuid(), PrincipalType = type, PrincipalId = principal ?? (type == PrincipalType.User ? "alice-oid" : "group-ops"),
        Role = role, EnvironmentName = env, ExpiresAt = expires, RevokedAt = revoked, Scope = scope,
    };

    private static AccessEvaluator Eval(params RoleAssignment[] a) => new(Alice, a, Now);

    [Fact]
    public void No_assignments_means_no_access_for_every_permission()
    {
        var e = Eval();
        foreach (var p in Enum.GetValues<Permission>()) Assert.False(e.Can(p), p.ToString());
    }

    [Fact]
    public void Platform_admin_can_do_everything()
    {
        var e = Eval(Assign(Role.PlatformAdmin));
        foreach (var p in Enum.GetValues<Permission>()) Assert.True(e.Can(p, "Prod"), p.ToString());
    }

    [Fact]
    public void Auditor_is_read_only()
    {
        var e = Eval(Assign(Role.Auditor));
        Assert.True(e.Can(Permission.ViewServers));
        Assert.True(e.Can(Permission.ViewAudit));
        Assert.False(e.Can(Permission.ManageServers));
        Assert.False(e.Can(Permission.ManageIis));
        Assert.False(e.Can(Permission.ManageRoles));
    }

    [Fact]
    public void Server_operator_is_limited_to_the_environment_it_was_granted()
    {
        var e = Eval(Assign(Role.ServerOperator, env: "Dev"));
        Assert.True(e.Can(Permission.ManageIis, "Dev"));
        Assert.False(e.Can(Permission.ManageIis, "Prod"));
        Assert.False(e.Can(Permission.ManageServers));      // adding servers is admin only
        Assert.Equal(new[] { "Dev" }, e.AllowedEnvironments(Permission.ViewServers)!.ToArray());
    }

    [Fact]
    public void Server_operator_with_all_environments_is_unscoped()
    {
        var e = Eval(Assign(Role.ServerOperator, env: null));
        Assert.True(e.Can(Permission.ManageIis, "Prod"));
        Assert.Null(e.AllowedEnvironments(Permission.ViewServers));
    }

    [Fact]
    public void Group_assignments_apply_through_group_membership()
    {
        Assert.True(Eval(Assign(Role.Auditor, PrincipalType.Group, "group-ops")).Can(Permission.ViewAudit));
        Assert.False(Eval(Assign(Role.Auditor, PrincipalType.Group, "group-other")).Can(Permission.ViewAudit));
    }

    [Fact]
    public void Expired_and_revoked_assignments_are_ignored()
    {
        Assert.False(Eval(Assign(Role.PlatformAdmin, expires: Now.AddSeconds(-1))).Can(Permission.ManageServers));
        Assert.False(Eval(Assign(Role.PlatformAdmin, revoked: Now.AddDays(-1))).Can(Permission.ManageServers));
        Assert.True(Eval(Assign(Role.PlatformAdmin, expires: Now.AddDays(1))).Can(Permission.ManageServers));
    }

    [Fact]
    public void Project_roles_never_grant_platform_permissions()
    {
        var e = Eval(Assign(Role.Approver, scope: RoleScope.Project), Assign(Role.ProjectOwner, scope: RoleScope.Project));
        foreach (var p in Enum.GetValues<Permission>()) Assert.False(e.Can(p), p.ToString());
    }

    [Fact]
    public void Another_users_assignment_does_not_apply()
    {
        Assert.False(Eval(Assign(Role.PlatformAdmin, principal: "bob-oid")).Can(Permission.ManageServers));
    }

    [Fact]
    public void Principal_id_matching_ignores_case()
    {
        Assert.True(Eval(Assign(Role.Auditor, principal: "ALICE-OID")).Can(Permission.ViewAudit));
    }
}
