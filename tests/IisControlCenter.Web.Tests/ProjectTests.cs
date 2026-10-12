using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    [Fact]
    public async Task Admin_creates_a_project_and_its_owner_group_becomes_project_owner()
    {
        var id = await t.NewProject("Payments", "grp-pay-owners");
        var members = await t.Members.ListAsync(t.Admin, id);
        var owner = Assert.Single(members);
        Assert.Equal((Role.ProjectOwner, PrincipalType.Group, "grp-pay-owners"), (owner.Role, owner.PrincipalType, owner.PrincipalId));
        Assert.Null(owner.EnvironmentName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Role.ServerOperator)]
    [InlineData(Role.Auditor)]
    public async Task Only_platform_admin_creates_projects_and_denials_are_audited(Role? role)
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Projects.CreateAsync(t.ActorWith(role, "Dev"), "X1", null, null, null));
        await using var db = t.Db.CreateDbContext();
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Project.Create" && a.Result == "Denied");
    }

    [Fact]
    public async Task Project_names_must_be_unique_even_when_only_the_slug_collides()
    {
        await t.NewProject("Payments");
        Assert.False((await t.Projects.CreateAsync(t.Admin, "Payments", null, null, null)).Ok);
        Assert.False((await t.Projects.CreateAsync(t.Admin, "payments!", null, null, null)).Ok);   // same slug
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("ab", true)]
    public async Task Name_length_is_checked(string name, bool ok) => Assert.Equal(ok, (await t.Projects.CreateAsync(t.Admin, name, null, null, null)).Ok);

    [Fact]
    public async Task New_components_get_the_default_protected_paths()
    {
        var p = await t.NewProject(); var c = await t.NewComponent(p);
        var view = await t.Projects.GetAsync(t.Admin, p);
        var comp = Assert.Single(view!.Components);
        Assert.Contains(comp.ProtectedPaths, x => x.Path == "web.config" && x.Kind == ProtectedPathKind.ConfigFile && x.IsDefault);
        Assert.Contains(comp.ProtectedPaths, x => x.Path == "appsettings*.json" && x.Kind == ProtectedPathKind.ConfigFile);
        Assert.Contains(comp.ProtectedPaths, x => x.Path == "uploads\\" && x.Kind == ProtectedPathKind.DataFolder);
        Assert.Contains(comp.ProtectedPaths, x => x.Path == "generated-files\\" && x.Kind == ProtectedPathKind.DataFolder);
    }

    [Fact]
    public async Task Component_validation()
    {
        var p = await t.NewProject();
        Assert.False((await t.Projects.AddComponentAsync(t.Admin, p, "Web", "Web", "health", null, false)).Ok);          // path must start with /
        Assert.False((await t.Projects.AddComponentAsync(t.Admin, p, "Web", "Web", "/h", "exe; bad ext!", false)).Ok);  // odd extension
        Assert.True((await t.Projects.AddComponentAsync(t.Admin, p, "Web", "Web", "/h", "exe; .PS1", true)).Ok);
        Assert.False((await t.Projects.AddComponentAsync(t.Admin, p, "Web", "Web", "/h", null, false)).Ok);              // duplicate name
        var c = (await t.Projects.GetAsync(t.Admin, p))!.Components.Single();
        Assert.Equal(".exe;.ps1", c.AllowedFileTypes);
        Assert.True(c.RequiresStagingBeforeProd);
    }

    [Fact]
    public void File_type_parsing()
    {
        Assert.Equal(".exe;.ps1", ProjectService.ParseFileTypes("EXE, ps1 ;.exe", out var e1)); Assert.Null(e1);
        Assert.Null(ProjectService.ParseFileTypes("  ", out var e2)); Assert.Null(e2);
        ProjectService.ParseFileTypes("../x", out var e3); Assert.NotNull(e3);
    }

    [Fact]
    public async Task Prod_approver_count_is_one_or_two_and_self_approval_is_audited_and_off_by_default()
    {
        var p = await t.NewProject();
        Assert.False((await t.Projects.SetProdApproversAsync(t.Admin, p, 3)).Ok);
        Assert.True((await t.Projects.SetProdApproversAsync(t.Admin, p, 2)).Ok);
        var v = (await t.Projects.GetAsync(t.Admin, p))!;
        Assert.Equal(2, v.Project.ProdApproversRequired);
        Assert.All(v.SelfApproval.Values, on => Assert.False(on));
        Assert.True((await t.Projects.SetSelfApprovalAsync(t.Admin, p, "Dev", true)).Ok);
        Assert.True((await t.Projects.GetAsync(t.Admin, p))!.SelfApproval["Dev"]);
        Assert.False((await t.Projects.SetSelfApprovalAsync(t.Admin, p, "Moon", true)).Ok);
        await using var db = t.Db.CreateDbContext();
        var a = await db.AuditLog.SingleAsync(x => x.Action == "Project.SetSelfApproval");
        Assert.Equal(("False", "True", "Dev"), (a.Before, a.After, a.EnvironmentName));
    }

    [Fact]
    public async Task Project_owner_cannot_change_project_settings_or_self_approval()
    {
        var p = await t.NewProject("Payments", "g");
        var owner = OwnerOf(p);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Projects.SetSelfApprovalAsync(owner, p, "Prod", true));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Projects.AddComponentAsync(owner, p, "X1", "Web", "/", null, false));
    }

    private Actor OwnerOf(Guid projectId, string id = "owner1")
    {
        var user = new UserContext(id, id, []);
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Project, ProjectId = projectId, Role = Role.ProjectOwner };
        return new Actor(id, id, null, new AccessEvaluator(user, [a], t.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Component_with_targets_cannot_be_removed_but_an_empty_one_can()
    {
        var p = await t.NewProject(); var c = await t.NewComponent(p);
        var s = await t.ApprovedServer(); await t.Store(s, new TestEnv.SiteSpec(1, "a", @"D:\a", "A"));
        var target = await t.Link(c, s, 1);
        Assert.False((await t.Projects.RemoveComponentAsync(t.Admin, c)).Ok);
        await t.Targets.RemoveAsync(t.Admin, target);
        Assert.True((await t.Projects.RemoveComponentAsync(t.Admin, c)).Ok);
    }

    [Fact]
    public async Task Users_only_see_projects_and_environments_they_hold_roles_in()
    {
        var pay = await t.NewProject("Payments"); var inv = await t.NewProject("Inventory");
        var c1 = await t.NewComponent(pay); var c2 = await t.NewComponent(inv, "Web");
        var dev = await t.ApprovedServer("DEV1", "Dev"); var prd = await t.ApprovedServer("PRD1", "Prod");
        await t.Store(dev, new TestEnv.SiteSpec(1, "pay", @"D:\pay", "PayDev")); await t.Store(prd, new TestEnv.SiteSpec(1, "pay", @"D:\pay", "PayPrd"));
        await t.Link(c1, dev, 1); await t.Link(c1, prd, 1);

        var carol = ActorWithProjectRole("carol", pay, Role.ProjectViewer, "Dev");
        var list = await t.Projects.ListAsync(carol);
        Assert.Equal(["Payments"], list.Select(r => r.Name));
        var view = (await t.Projects.GetAsync(carol, pay))!;
        Assert.Equal(["Dev"], view.VisibleEnvironments);
        Assert.Equal(["Dev"], view.Components.Single().Targets.Select(x => x.EnvironmentName));       // the Prod target is hidden
        Assert.Null(await t.Projects.GetAsync(carol, inv));
        Assert.Equal(2, (await t.Projects.ListAsync(t.Admin)).Count);
        Assert.Equal(2, (await t.Projects.ListAsync(t.ActorWith(Role.Auditor))).Count);
        Assert.Empty(await t.Projects.ListAsync(t.Nobody));
    }

    private Actor ActorWithProjectRole(string id, Guid project, Role role, string? env)
    {
        var user = new UserContext(id, id, []);
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Project, ProjectId = project, Role = role, EnvironmentName = env };
        return new Actor(id, id, null, new AccessEvaluator(user, [a], t.Clock.GetUtcNow()));
    }
}

public class MembershipServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    private Actor As(string id, Guid project, Role role, string? env = null)
    {
        var user = new UserContext(id, id, []);
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Project, ProjectId = project, Role = role, EnvironmentName = env };
        return new Actor(id, id, null, new AccessEvaluator(user, [a], t.Clock.GetUtcNow()));
    }

    private Task<OpResult> Grant(Actor a, Guid p, Role role, string? env = "Dev", string who = "g1", PrincipalType type = PrincipalType.Group, DateTimeOffset? exp = null) =>
        t.Members.GrantAsync(a, p, type, who, "Name " + who, role, env, exp);

    [Fact]
    public async Task Admin_can_grant_every_project_role()
    {
        var p = await t.NewProject();
        Assert.True((await Grant(t.Admin, p, Role.Deployer, "Dev", "a")).Ok);
        Assert.True((await Grant(t.Admin, p, Role.Approver, "Prod", "b")).Ok);
        Assert.True((await Grant(t.Admin, p, Role.ProjectViewer, "Staging", "c")).Ok);
        Assert.True((await Grant(t.Admin, p, Role.ProjectOwner, null, "d")).Ok);
        Assert.Equal(4, (await t.Members.ListAsync(t.Admin, p)).Count);
    }

    [Fact]
    public async Task Owner_can_grant_up_to_Deployer_only_and_the_refusal_is_audited()
    {
        var p = await t.NewProject();
        var owner = As("o", p, Role.ProjectOwner);
        Assert.True((await Grant(owner, p, Role.ProjectViewer)).Ok);
        Assert.True((await Grant(owner, p, Role.Deployer, "Prod", "g2")).Ok);
        var approver = await Grant(owner, p, Role.Approver, "Dev", "g3");
        Assert.False(approver.Ok);
        Assert.Contains("Platform Admin", approver.Error);
        Assert.False((await Grant(owner, p, Role.ProjectOwner, null, "g4")).Ok);
        await using var db = t.Db.CreateDbContext();
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "ProjectRole.Grant" && a.Result == "Denied" && a.Detail!.Contains("Approver"));
    }

    [Fact]
    public async Task Approver_on_Production_cannot_be_granted_by_an_owner_and_nobody_else_can_grant_anything()
    {
        var p = await t.NewProject();
        Assert.False((await Grant(As("o", p, Role.ProjectOwner), p, Role.Approver, "Prod")).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => Grant(As("d", p, Role.Deployer, "Dev"), p, Role.ProjectViewer));
        await Assert.ThrowsAsync<ForbiddenException>(() => Grant(t.Nobody, p, Role.ProjectViewer));
        await Assert.ThrowsAsync<ForbiddenException>(() => Grant(t.ActorWith(Role.Auditor), p, Role.ProjectViewer));
    }

    [Fact]
    public async Task Owner_of_another_project_has_no_power_here()
    {
        var a = await t.NewProject("A1"); var b = await t.NewProject("B1");
        await Assert.ThrowsAsync<ForbiddenException>(() => Grant(As("o", a, Role.ProjectOwner), b, Role.ProjectViewer));
    }

    [Fact]
    public async Task Users_need_an_expiry_groups_do_not_and_expiry_must_be_future()
    {
        var p = await t.NewProject();
        Assert.False((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User)).Ok);
        Assert.False((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User, t.Clock.GetUtcNow().AddMinutes(-1))).Ok);
        Assert.True((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User, t.Clock.GetUtcNow().AddDays(30))).Ok);
        Assert.True((await Grant(t.Admin, p, Role.Deployer, "Dev", "g1")).Ok);
    }

    [Fact]
    public async Task Environment_rules_per_role()
    {
        var p = await t.NewProject();
        Assert.False((await Grant(t.Admin, p, Role.Deployer, null)).Ok);                 // needs an environment
        Assert.False((await Grant(t.Admin, p, Role.Deployer, "Moon")).Ok);
        Assert.True((await Grant(t.Admin, p, Role.ProjectOwner, "Prod", "o1")).Ok);       // owner is project-wide: environment ignored
        Assert.Null((await t.Members.ListAsync(t.Admin, p)).Single().EnvironmentName);
    }

    [Fact]
    public async Task Duplicate_active_grants_are_refused_but_an_expired_one_can_be_replaced()
    {
        var p = await t.NewProject();
        Assert.True((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User, t.Clock.GetUtcNow().AddDays(1))).Ok);
        Assert.False((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User, t.Clock.GetUtcNow().AddDays(5))).Ok);
        t.Clock.Advance(TimeSpan.FromDays(2));
        Assert.True((await Grant(t.Admin, p, Role.Deployer, "Dev", "u1", PrincipalType.User, t.Clock.GetUtcNow().AddDays(5))).Ok);
    }

    [Fact]
    public async Task Revoke_follows_the_same_rules_and_is_audited()
    {
        var p = await t.NewProject();
        await Grant(t.Admin, p, Role.Deployer, "Dev", "a"); await Grant(t.Admin, p, Role.Approver, "Dev", "b");
        var rows = await t.Members.ListAsync(t.Admin, p);
        var deployer = rows.Single(r => r.Role == Role.Deployer); var approver = rows.Single(r => r.Role == Role.Approver);
        var owner = As("o", p, Role.ProjectOwner);
        Assert.False((await t.Members.RevokeAsync(owner, approver.Id)).Ok);          // owner cannot remove an Approver
        Assert.True((await t.Members.RevokeAsync(owner, deployer.Id)).Ok);
        Assert.True((await t.Members.RevokeAsync(t.Admin, approver.Id)).Ok);
        Assert.Empty(await t.Members.ListAsync(t.Admin, p));
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(2, await db.AuditLog.CountAsync(a => a.Action == "ProjectRole.Revoke" && a.Result == "Success"));
        // a plain Deployer cannot revoke anything
        await Grant(t.Admin, p, Role.Deployer, "Dev", "z");
        var z = (await t.Members.ListAsync(t.Admin, p)).Single();
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Members.RevokeAsync(As("d", p, Role.Deployer, "Dev"), z.Id));
    }

    [Fact]
    public async Task Member_list_flags_which_rows_the_viewer_may_revoke_and_hides_other_environments_from_viewers()
    {
        var p = await t.NewProject();
        await Grant(t.Admin, p, Role.Deployer, "Dev", "a"); await Grant(t.Admin, p, Role.Approver, "Prod", "b");
        var asOwner = await t.Members.ListAsync(As("o", p, Role.ProjectOwner), p);
        Assert.True(asOwner.Single(r => r.Role == Role.Deployer).CanRevoke);
        Assert.False(asOwner.Single(r => r.Role == Role.Approver).CanRevoke);
        var asViewer = await t.Members.ListAsync(As("v", p, Role.ProjectViewer, "Dev"), p);
        Assert.Equal(["Dev"], asViewer.Select(r => r.EnvironmentName));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Members.ListAsync(t.Nobody, p));
    }

    [Fact]
    public async Task Platform_role_service_refuses_project_roles_and_membership_service_refuses_platform_roles()
    {
        var p = await t.NewProject();
        Assert.False((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.Deployer, "Dev", null)).Ok);
        Assert.False((await Grant(t.Admin, p, Role.PlatformAdmin, null)).Ok);
        Assert.Empty(await t.Roles.ListPlatformAsync(t.Admin));      // project grants never show up in the platform list
    }

    [Fact]
    public async Task My_access_lists_roles_with_their_source()
    {
        var p = await t.NewProject("Payments");
        var user = new UserContext("me", "Me", ["grp"]);
        var a1 = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.Group, PrincipalId = "grp", PrincipalName = "Pay devs", Scope = RoleScope.Project, ProjectId = p, Role = Role.Deployer, EnvironmentName = "Dev" };
        var a2 = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "me", Scope = RoleScope.Platform, Role = Role.Auditor };
        var rows = await t.Members.MyAccessAsync(new Actor("me", "Me", null, new AccessEvaluator(user, [a1, a2], t.Clock.GetUtcNow())));
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Scope == "Project" && r.ProjectName == "Payments" && r.Source == "Group: Pay devs" && r.EnvironmentName == "Dev");
        Assert.Contains(rows, r => r.Scope == "Platform" && r.Role == Role.Auditor && r.Source == "Directly assigned");
    }
}
