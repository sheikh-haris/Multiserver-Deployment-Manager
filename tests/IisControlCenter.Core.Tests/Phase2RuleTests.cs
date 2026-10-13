using IisControlCenter.Core.Discovery;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Projects;
using IisControlCenter.Core.Security;
using IisControlCenter.Core.Targets;

namespace IisControlCenter.Core.Tests;

public class LinkRulesTests
{
    private static readonly Guid Srv = Guid.NewGuid(), P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), C1 = Guid.NewGuid(), C2 = Guid.NewGuid(), C3 = Guid.NewGuid();

    private static LinkCandidate Cand(Guid? comp = null, Guid? proj = null, string env = "Dev", string? serverEnv = "Dev", long site = 10, string app = "/",
        string path = @"D:\Websites\pay\web", string pool = "PayPool") =>
        new(comp ?? C1, proj ?? P1, env, Srv, serverEnv, site, app, path, pool);

    private static ExistingLink Ex(Guid? comp = null, Guid? proj = null, string cname = "Other", string pname = "Billing", string env = "Dev",
        long site = 99, string app = "/", string path = @"D:\Websites\billing\web", string pool = "BillPool") =>
        new(Guid.NewGuid(), comp ?? C2, cname, proj ?? P2, pname, env, site, app, path, pool);

    private static LinkViolationCode[] Codes(LinkCandidate c, params ExistingLink[] e) => LinkRules.Check(c, e).Select(v => v.Code).ToArray();

    [Fact] public void Clean_link_has_no_violations() => Assert.Empty(Codes(Cand(), Ex()));

    [Fact] public void Unapproved_server_is_blocked() => Assert.Contains(LinkViolationCode.ServerNotApproved, Codes(Cand(serverEnv: null)));

    [Fact]
    public void Target_environment_must_match_the_servers_environment()
    {
        var v = LinkRules.Check(Cand(env: "Prod", serverEnv: "Dev"), []);
        Assert.Contains(v, x => x.Code == LinkViolationCode.EnvironmentMismatch && !x.Exceptionable);
    }

    [Fact]
    public void Same_site_and_path_cannot_be_linked_twice_even_with_different_case_or_slash()
    {
        Assert.Contains(LinkViolationCode.SiteAlreadyLinked, Codes(Cand(site: 10, app: "/api"), Ex(site: 10, app: "/API/")));
        Assert.DoesNotContain(LinkViolationCode.SiteAlreadyLinked, Codes(Cand(site: 10, app: "/api", path: @"D:\W\a\api"), Ex(site: 10, app: "/", path: @"D:\W\b")));
    }

    [Fact]
    public void Pool_used_by_another_component_is_an_exceptionable_violation_and_says_whether_it_crosses_projects()
    {
        var other = LinkRules.Check(Cand(pool: "Shared"), [Ex(pool: "SHARED")]);
        var v = Assert.Single(other, x => x.Code == LinkViolationCode.PoolUsedByOtherComponent);
        Assert.True(v.Exceptionable);
        Assert.Contains("across projects", v.Message);
        var same = LinkRules.Check(Cand(pool: "Shared"), [Ex(pool: "Shared", proj: P1)]);
        Assert.DoesNotContain("across projects", same.Single(x => x.Code == LinkViolationCode.PoolUsedByOtherComponent).Message);
    }

    [Fact]
    public void The_same_component_may_reuse_its_own_pool_on_the_same_server()
    {
        Assert.DoesNotContain(LinkViolationCode.PoolUsedByOtherComponent, Codes(Cand(pool: "P"), Ex(comp: C1, pool: "P", env: "Staging", path: @"D:\x\y")));
    }

    [Theory]
    [InlineData(@"D:\Websites\pay", @"D:\Websites\pay\api", true)]      // parent and child
    [InlineData(@"D:\Websites\pay\api", @"D:\Websites\pay", true)]
    [InlineData(@"D:\WEBSITES\Pay\", @"d:/websites/pay", true)]          // same folder, other spelling
    [InlineData(@"D:\Websites\app1", @"D:\Websites\app10", false)]       // prefix of the name is not a parent
    [InlineData(@"D:\Websites\a", @"D:\Websites\b", false)]
    [InlineData(@"D:\Websites", @"E:\Websites", false)]
    [InlineData("", @"D:\x", false)]
    public void Folder_overlap(string a, string b, bool overlap) => Assert.Equal(overlap, LinkRules.PathsOverlap(a, b));

    [Fact]
    public void The_same_folder_twice_is_blocked_and_cannot_be_waived()
    {
        var v = LinkRules.Check(Cand(path: @"D:\Websites\billing"), [Ex(path: @"D:\WEBSITES\billing\")]);
        Assert.Contains(v, x => x.Code == LinkViolationCode.PathOverlap && !x.Exceptionable);
    }

    [Fact]
    public void A_sub_application_in_a_sub_folder_of_its_site_is_normal_and_allowed()
    {
        Assert.DoesNotContain(LinkViolationCode.PathOverlap, Codes(Cand(path: @"D:\Websites\billing\api"), Ex(path: @"D:\Websites\billing")));
        Assert.DoesNotContain(LinkViolationCode.PathOverlap, Codes(Cand(path: @"D:\Websites\billing"), Ex(path: @"D:\Websites\billing\api")));
    }

    [Fact]
    public void A_component_can_have_only_one_target_per_environment_on_a_server()
    {
        Assert.Contains(LinkViolationCode.ComponentAlreadyOnServer, Codes(Cand(comp: C1), Ex(comp: C1, cname: "Web")));
    }

    [Fact]
    public void Item_without_path_or_pool_is_blocked() =>
        Assert.Contains(LinkViolationCode.MissingData, Codes(Cand(path: "", pool: "")));

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("api", "/api")]
    [InlineData("/api/", "/api")]
    [InlineData(@"\api\v1", "/api/v1")]
    [InlineData("/", "/")]
    public void App_paths_are_normalised(string? input, string expected) => Assert.Equal(expected, LinkRules.NormalizeAppPath(input));
}

public class TargetVerifierTests
{
    private static DeploymentTarget T(string path = @"D:\Websites\pay", string pool = "PayPool", string app = "/", string name = "pay-web") =>
        new() { IisSiteId = 5, IisSiteName = name, AppPath = app, PhysicalPath = path, AppPoolName = pool };

    private static IisSite S(string name = "pay-web", string path = @"D:\Websites\pay", string pool = "PayPool", string appPath = "/") =>
        new() { IisSiteId = 5, Name = name, Applications = [new IisApplication { Path = appPath, PhysicalPath = path, AppPoolName = pool }] };

    [Fact] public void Matching_site_is_active() => Assert.Equal(TargetStatus.Active, TargetVerifier.Verify(T(), S()).Status);

    [Fact] public void Comparison_ignores_case_and_slash_style() =>
        Assert.Equal(TargetStatus.Active, TargetVerifier.Verify(T(), S(path: @"d:/WEBSITES/Pay/", pool: "paypool")).Status);

    [Fact]
    public void Deleted_site_is_missing()
    {
        var r = TargetVerifier.Verify(T(), null);
        Assert.Equal(TargetStatus.Missing, r.Status);
        Assert.Contains("no longer exists", r.Detail);
    }

    [Fact]
    public void Renamed_site_is_missing()
    {
        var r = TargetVerifier.Verify(T(), S(name: "pay-web-new"));
        Assert.Equal(TargetStatus.Missing, r.Status);
        Assert.Contains("renamed", r.Detail);
    }

    [Fact] public void Missing_application_is_missing() => Assert.Equal(TargetStatus.Missing, TargetVerifier.Verify(T(app: "/api"), S()).Status);

    [Fact]
    public void Changed_folder_or_pool_is_drift_with_details()
    {
        var folder = TargetVerifier.Verify(T(), S(path: @"D:\Other"));
        Assert.Equal(TargetStatus.Drifted, folder.Status);
        Assert.Contains(@"D:\Other", folder.Detail);
        var pool = TargetVerifier.Verify(T(), S(pool: "NewPool"));
        Assert.Equal(TargetStatus.Drifted, pool.Status);
        Assert.Contains("NewPool", pool.Detail);
        var both = TargetVerifier.Verify(T(), S(path: @"D:\Other", pool: "NewPool"));
        Assert.Contains("folder", both.Detail); Assert.Contains("app pool", both.Detail);
    }

    [Fact]
    public void Sub_application_is_found_by_path()
    {
        var site = new IisSite { IisSiteId = 5, Name = "pay-web", Applications = [new IisApplication { Path = "/", PhysicalPath = @"D:\r", AppPoolName = "A" }, new IisApplication { Path = "/api", PhysicalPath = @"D:\r\api", AppPoolName = "B" }] };
        Assert.Equal(TargetStatus.Active, TargetVerifier.Verify(T(path: @"D:\r\api", pool: "B", app: "/api"), site).Status);
    }
}

public class SnapshotDifferTests
{
    private static IisSite Site(long id, string name, string path = @"D:\a", string pool = "P", string state = "Started", params string[] hosts) =>
        new() { IisSiteId = id, Name = name, PhysicalPath = path, AppPoolName = pool, State = state,
                Bindings = hosts.Select(h => new IisBinding { Protocol = "http", Host = h, Port = 80 }).ToList(),
                Applications = [new IisApplication { Path = "/" }] };

    private static List<DriftItem> Diff(IisSite[] o, IisSite[] n, IisAppPool[]? op = null, IisAppPool[]? np = null, IisCertificate[]? oc = null, IisCertificate[]? nc = null) =>
        SnapshotDiffer.Diff(o, n, op ?? [], np ?? [], oc ?? [], nc ?? []);

    [Fact] public void Identical_snapshots_have_no_drift() => Assert.Empty(Diff([Site(1, "a", hosts: "x")], [Site(1, "a", hosts: "x")]));

    [Fact] public void Started_or_stopped_is_not_drift() => Assert.Empty(Diff([Site(1, "a")], [Site(1, "a", state: "Stopped")]));

    [Fact]
    public void Detects_created_and_deleted_sites()
    {
        var d = Diff([Site(1, "old")], [Site(2, "new")]);
        Assert.Contains(d, x => x.Kind == DriftKind.Deleted && x.ObjectRef == "old");
        Assert.Contains(d, x => x.Kind == DriftKind.Created && x.ObjectRef == "new");
    }

    [Fact]
    public void Detects_rename_folder_pool_and_binding_changes_in_one_item()
    {
        var d = Assert.Single(Diff([Site(1, "a", @"D:\a", "P", hosts: "x")], [Site(1, "b", @"D:\b", "Q", hosts: "y")]));
        Assert.Equal(DriftKind.Changed, d.Kind);
        foreach (var part in new[] { "name 'a' -> 'b'", "folder", "app pool", "bindings" }) Assert.Contains(part, d.After);
    }

    [Fact]
    public void Detects_pool_changes_and_certificate_changes()
    {
        var oldPool = new IisAppPool { Name = "P", IdentityType = "ApplicationPoolIdentity", RuntimeVersion = "v4.0", PipelineMode = "Integrated" };
        var newPool = new IisAppPool { Name = "P", IdentityType = "LocalSystem", RuntimeVersion = "v4.0", PipelineMode = "Integrated" };
        var d = Diff([], [], [oldPool], [newPool], [new IisCertificate { Thumbprint = "aa", Subject = "old" }], [new IisCertificate { Thumbprint = "BB", Subject = "new" }]);
        Assert.Contains(d, x => x.ObjectType == "AppPool" && x.After!.Contains("identity ApplicationPoolIdentity -> LocalSystem"));
        Assert.Contains(d, x => x.ObjectType == "Certificate" && x.Kind == DriftKind.Created);
        Assert.Contains(d, x => x.ObjectType == "Certificate" && x.Kind == DriftKind.Deleted);
    }

    [Fact] public void Certificate_thumbprints_compare_ignoring_case() =>
        Assert.Empty(Diff([], [], null, null, [new IisCertificate { Thumbprint = "abcd" }], [new IisCertificate { Thumbprint = "ABCD" }]));
}

public class MappingSuggesterTests
{
    private static readonly KnownProject[] Known =
    [
        new("Payments", ["Web Frontend", "Backend API"]),
        new("Inventory", ["Web"]),
    ];

    [Fact]
    public void Folder_project_slash_component_matches_a_known_component_with_high_confidence()
    {
        var s = MappingSuggester.Suggest("Site1", [], @"D:\Websites\Payments\Backend API", Known);
        Assert.Equal(("Payments", "Backend API", Confidence.High), (s.ProjectName, s.ComponentName, s.Confidence));
    }

    [Fact]
    public void Folder_with_a_known_project_but_new_component_is_medium()
    {
        var s = MappingSuggester.Suggest("Site1", [], @"D:\Websites\payments\mobile", Known);
        Assert.Equal(("Payments", "mobile", Confidence.Medium), (s.ProjectName, s.ComponentName, s.Confidence));
    }

    [Fact]
    public void Site_name_project_component_env_convention_is_understood()
    {
        var s = MappingSuggester.Suggest("payments-web-frontend-dev", [], null, Known);
        Assert.Equal("Payments", s.ProjectName);
        Assert.Equal("Web Frontend", s.ComponentName);
        Assert.Equal(Confidence.High, s.Confidence);
        Assert.Equal("Payments", MappingSuggester.Suggest("Payments_Backend-API_prod", [], null, Known).ProjectName);
    }

    [Fact]
    public void Host_name_containing_a_project_name_gives_a_low_confidence_project_only_hint()
    {
        var s = MappingSuggester.Suggest("Default Web Site", ["payments.corp.local"], @"C:\inetpub\wwwroot", Known);
        Assert.Equal("Payments", s.ProjectName);
        Assert.Null(s.ComponentName);
        Assert.Equal(Confidence.Low, s.Confidence);
    }

    [Fact]
    public void Unknown_things_get_no_suggestion_and_never_invent_a_project()
    {
        var s = MappingSuggester.Suggest("legacy-portal", ["legacy.local"], @"D:\Websites\legacy", Known);
        Assert.Equal(Confidence.None, s.Confidence);
        Assert.Null(s.ProjectName);
        Assert.Equal(Confidence.None, MappingSuggester.Suggest("x", [], @"C:\inetpub\wwwroot", []).Confidence);
    }

    [Fact] public void Very_short_project_names_are_not_matched_by_host_substrings() =>
        Assert.Equal(Confidence.None, MappingSuggester.Suggest("x", ["abc.local"], null, [new KnownProject("ab", [])]).Confidence);
}

public class ProjectAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Pay = Guid.NewGuid(), Inv = Guid.NewGuid();
    private static readonly UserContext Alice = new("alice", "Alice", ["grp"]);

    private static RoleAssignment A(Role role, Guid? project = null, string? env = null, PrincipalType type = PrincipalType.User, RoleScope scope = RoleScope.Project, DateTimeOffset? expires = null) =>
        new() { Id = Guid.NewGuid(), PrincipalType = type, PrincipalId = type == PrincipalType.User ? "alice" : "grp", Role = role, ProjectId = scope == RoleScope.Project ? project : null, EnvironmentName = env, Scope = scope, ExpiresAt = expires };

    private static AccessEvaluator E(params RoleAssignment[] a) => new(Alice, a, Now);

    [Fact]
    public void Example_from_the_brief_Deployer_and_Approver_on_Dev_but_only_Deployer_on_Prod()
    {
        var e = E(A(Role.Deployer, Pay, "Dev"), A(Role.Approver, Pay, "Dev"), A(Role.Deployer, Pay, "Prod"));
        Assert.True(e.CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
        Assert.True(e.CanOnProject(ProjectPermission.ApproveDeployment, Pay, "Dev"));
        Assert.True(e.CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Prod"));
        Assert.False(e.CanOnProject(ProjectPermission.ApproveDeployment, Pay, "Prod"));
        Assert.False(e.CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Staging"));
    }

    [Fact]
    public void Roles_do_not_leak_to_other_projects()
    {
        var e = E(A(Role.Deployer, Pay, "Dev"));
        Assert.False(e.CanOnProject(ProjectPermission.SubmitDeployment, Inv, "Dev"));
        Assert.False(e.CanOnProject(ProjectPermission.View, Inv));
    }

    [Fact]
    public void Environment_scoped_actions_need_an_environment()
    {
        var e = E(A(Role.Deployer, Pay, "Dev"));
        Assert.False(e.CanOnProject(ProjectPermission.SubmitDeployment, Pay));   // no environment given
        Assert.True(e.CanOnProject(ProjectPermission.View, Pay));                // viewing a project needs no environment
    }

    [Fact]
    public void Each_project_role_grants_only_what_the_brief_says()
    {
        Assert.False(E(A(Role.ProjectViewer, Pay, "Dev")).CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
        Assert.False(E(A(Role.Deployer, Pay, "Dev")).CanOnProject(ProjectPermission.ApproveDeployment, Pay, "Dev"));
        Assert.False(E(A(Role.Approver, Pay, "Dev")).CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
        var owner = E(A(Role.ProjectOwner, Pay));
        Assert.True(owner.CanOnProject(ProjectPermission.ManageMembers, Pay));
        Assert.True(owner.CanOnProject(ProjectPermission.RequestLink, Pay));
        Assert.False(owner.CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));   // owner is not automatically a deployer
        Assert.False(owner.CanOnProject(ProjectPermission.ApproveDeployment, Pay, "Dev"));
    }

    [Fact]
    public void Group_assignments_work_and_expired_ones_do_not()
    {
        Assert.True(E(A(Role.Deployer, Pay, "Dev", PrincipalType.Group)).CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
        Assert.False(E(A(Role.Deployer, Pay, "Dev", expires: Now.AddSeconds(-1))).CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
    }

    [Fact]
    public void Platform_roles_never_give_deploy_or_approve_rights()
    {
        var admin = E(A(Role.PlatformAdmin, scope: RoleScope.Platform));
        Assert.False(admin.CanOnProject(ProjectPermission.SubmitDeployment, Pay, "Dev"));
        Assert.False(admin.CanOnProject(ProjectPermission.ApproveDeployment, Pay, "Dev"));
        Assert.True(admin.CanOnProject(ProjectPermission.View, Pay));
        Assert.True(admin.CanOnProject(ProjectPermission.ManageMembers, Pay));
        var op = E(A(Role.ServerOperator, scope: RoleScope.Platform, env: "Dev"));
        Assert.False(op.CanOnProject(ProjectPermission.View, Pay));
    }

    [Fact]
    public void Auditor_can_view_every_project_but_do_nothing_else()
    {
        var a = E(A(Role.Auditor, scope: RoleScope.Platform));
        Assert.True(a.CanOnProject(ProjectPermission.View, Pay));
        Assert.False(a.CanOnProject(ProjectPermission.ManageMembers, Pay));
        Assert.False(a.CanOnProject(ProjectPermission.RequestLink, Pay));
    }

    [Fact]
    public void Project_roles_never_grant_platform_permissions()
    {
        var e = E(A(Role.ProjectOwner, Pay), A(Role.Approver, Pay, "Prod"));
        Assert.False(e.Can(Permission.ViewServers));
        Assert.False(e.Can(Permission.ManageProjects));
        Assert.False(e.Can(Permission.ManageTargets));
    }

    [Fact]
    public void Helpers_list_environments_and_projects()
    {
        var e = E(A(Role.Deployer, Pay, "Dev"), A(Role.ProjectViewer, Pay, "Prod"), A(Role.ProjectOwner, Inv));
        Assert.Equal(["Dev", "Prod"], e.EnvironmentsFor(ProjectPermission.View, Pay).OrderBy(x => x));
        Assert.Equal(["Dev"], e.EnvironmentsFor(ProjectPermission.SubmitDeployment, Pay));
        Assert.Equal(2, e.ProjectIdsWithAnyRole().Count);
        Assert.True(e.IsProjectOwner(Inv));
        Assert.False(e.IsProjectOwner(Pay));
    }
}


public class GrantRulesTests
{
    [Theory]
    [InlineData(true, false, Role.ProjectViewer, true)]
    [InlineData(true, false, Role.Deployer, true)]
    [InlineData(true, false, Role.Approver, true)]
    [InlineData(true, false, Role.ProjectOwner, true)]
    [InlineData(false, true, Role.ProjectViewer, true)]
    [InlineData(false, true, Role.Deployer, true)]
    [InlineData(false, true, Role.Approver, false)]        // only a Platform Admin grants Approver
    [InlineData(false, true, Role.ProjectOwner, false)]
    [InlineData(false, false, Role.ProjectViewer, false)]  // not a member manager
    [InlineData(false, false, Role.Deployer, false)]
    public void Who_can_grant_what(bool admin, bool owner, Role role, bool allowed) =>
        Assert.Equal(allowed, GrantRules.Check(admin, owner, role) is null);

    [Theory]
    [InlineData(Role.PlatformAdmin)]
    [InlineData(Role.ServerOperator)]
    [InlineData(Role.Auditor)]
    public void Platform_roles_are_never_project_grants(Role role) => Assert.NotNull(GrantRules.Check(true, true, role));

    [Fact]
    public void Only_a_deployer_may_recycle_pools_of_the_project_and_only_in_their_environment()
    {
        foreach (var role in new[] { Role.ProjectViewer, Role.Approver, Role.ProjectOwner })
            Assert.False(ProjectRolePermissions.Grants(role, ProjectPermission.RecyclePool), role.ToString());
        Assert.True(ProjectRolePermissions.Grants(Role.Deployer, ProjectPermission.RecyclePool));
    }
}
