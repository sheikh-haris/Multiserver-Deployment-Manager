using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Projects;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public class TargetServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    private async Task<DeploymentTarget> Load(Guid id) { await using var db = t.Db.CreateDbContext(); return await db.DeploymentTargets.AsNoTracking().FirstAsync(x => x.Id == id); }
    private async Task<List<string>> Audit(string? result = null) { await using var db = t.Db.CreateDbContext(); return await db.AuditLog.Where(a => result == null || a.Result == result).OrderBy(a => a.Id).Select(a => a.Action + ":" + a.Result).ToListAsync(); }

    private async Task<(Guid Project, Guid Comp, Guid Server)> Basic(string env = "Dev")
    {
        var p = await t.NewProject(); var c = await t.NewComponent(p); var s = await t.ApprovedServer("SRV-" + env, env);
        return (p, c, s);
    }

    private Actor Owner(Guid project)
    {
        var user = new UserContext("owner", "Owner", []);
        var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "owner", Scope = RoleScope.Project, ProjectId = project, Role = Role.ProjectOwner };
        return new Actor("owner", "Owner", null, new AccessEvaluator(user, [a], t.Clock.GetUtcNow()));
    }

    // ---------- linking ----------

    [Fact]
    public async Task Link_stores_the_stable_identity_and_values_from_discovery_not_from_the_caller()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(7, "pay-web-dev", @"D:\Websites\pay\web", "PayWebPool"));
        var id = await t.Link(c, s, 7);
        var tg = await Load(id);
        Assert.Equal((s, 7L, "/", "Dev"), (tg.ServerId, tg.IisSiteId, tg.AppPath, tg.EnvironmentName));
        Assert.Equal((@"D:\Websites\pay\web", "PayWebPool", "pay-web-dev"), (tg.PhysicalPath, tg.AppPoolName, tg.IisSiteName));
        Assert.Equal((TargetStatus.Active, 1), (tg.Status, tg.DeployOrder));
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(MappingStatus.Mapped, (await db.IisSites.SingleAsync()).MappingStatus);
        Assert.Contains("Target.Link:Success", await Audit());
    }

    [Fact]
    public async Task Environment_comes_from_the_server_so_a_Dev_server_can_never_hold_a_Prod_target()
    {
        var (_, c, s) = await Basic("Dev");
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var tg = await Load(await t.Link(c, s, 1));
        Assert.Equal("Dev", tg.EnvironmentName);
    }

    [Fact]
    public async Task Only_platform_admin_can_link_and_the_denial_is_audited()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        foreach (var actor in new[] { t.Nobody, t.ActorWith(Role.ServerOperator, "Dev"), Owner(p) })
            await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.LinkAsync(actor, c, s, 1, "/"));
        Assert.Contains("Target.Link:Denied", await Audit());
    }

    [Fact]
    public async Task Unknown_site_or_application_or_unapproved_server_cannot_be_linked()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        Assert.False((await t.Targets.LinkAsync(t.Admin, c, s, 99, "/")).Ok);
        Assert.False((await t.Targets.LinkAsync(t.Admin, c, s, 1, "/nope")).Ok);
        var (pending, _) = await t.RegisteredServer("PEND");
        Assert.False((await t.Targets.LinkAsync(t.Admin, c, pending, 1, "/")).Ok);
        Assert.False((await t.Targets.LinkAsync(t.Admin, Guid.NewGuid(), s, 1, "/")).Ok);
    }

    [Fact]
    public async Task An_IIS_site_cannot_belong_to_two_components()
    {
        var (p, c1, s) = await Basic();
        var c2 = await t.NewComponent(p, "Backend API");
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        await t.Link(c1, s, 1);
        var r = await t.Targets.LinkAsync(t.Admin, c2, s, 1, "/");
        Assert.False(r.Ok);
        Assert.Contains("already linked", r.Error);
    }

    [Fact]
    public async Task Pool_shared_across_projects_is_blocked_unless_an_admin_records_a_reason()
    {
        var p1 = await t.NewProject("Payments"); var p2 = await t.NewProject("Inventory");
        var c1 = await t.NewComponent(p1); var c2 = await t.NewComponent(p2, "Web");
        var s = await t.ApprovedServer();
        await t.Store(s, new SiteSpec(1, "pay", @"D:\pay", "DefaultAppPool"), new SiteSpec(2, "inv", @"D:\inv", "DefaultAppPool"));
        await t.Link(c1, s, 1);

        var blocked = await t.Targets.LinkAsync(t.Admin, c2, s, 2, "/");
        Assert.False(blocked.Ok);
        Assert.Contains("across projects", blocked.Error);
        Assert.Contains("giving a reason", blocked.Error);

        var allowed = await t.Targets.LinkAsync(t.Admin, c2, s, 2, "/", "Legacy server, will be split in Q1");
        Assert.True(allowed.Ok, allowed.Error);
        await using var db = t.Db.CreateDbContext();
        var ex = await db.LinkExceptions.SingleAsync();
        Assert.Equal("Legacy server, will be split in Q1", ex.Reason);
        Assert.Contains("DefaultAppPool", ex.Violation);
        Assert.NotNull((await Load(allowed.Value)).LinkExceptionId);
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Target.Link" && a.After!.Contains("EXCEPTION"));
    }

    [Fact]
    public async Task The_same_folder_twice_is_blocked_even_with_an_exception_reason()
    {
        var (p, c1, s) = await Basic();
        var c2 = await t.NewComponent(p, "Backend API");
        await t.Store(s, new SiteSpec(1, "root", @"D:\Websites\pay", "A", ("/api", @"D:\Websites\pay", "B")));
        await t.Link(c1, s, 1, "/");
        var r = await t.Targets.LinkAsync(t.Admin, c2, s, 1, "/api", "please");
        Assert.False(r.Ok);
        Assert.Contains("exactly this folder", r.Error);
    }

    [Fact]
    public async Task A_sub_application_in_a_sub_folder_of_its_site_can_be_its_own_component()
    {
        var (p, c1, s) = await Basic();
        var c2 = await t.NewComponent(p, "Backend API");
        await t.Store(s, new SiteSpec(1, "root", @"D:\Websites\pay", "A", ("/api", @"D:\Websites\pay\api", "B")));
        await t.Link(c1, s, 1, "/");
        var r = await t.Targets.LinkAsync(t.Admin, c2, s, 1, "/api", null);
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task One_target_per_component_environment_and_server()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        await t.Link(c, s, 1);
        var r = await t.Targets.LinkAsync(t.Admin, c, s, 2, "/");
        Assert.False(r.Ok);
        Assert.Contains("already has", r.Error);
    }

    [Fact]
    public async Task Load_balanced_servers_get_a_deploy_order_that_can_be_changed()
    {
        var p = await t.NewProject(); var c = await t.NewComponent(p);
        var ids = new List<Guid>();
        foreach (var n in new[] { "PRD1", "PRD2", "PRD3" })
        {
            var s = await t.ApprovedServer(n, "Prod");
            await t.Store(s, new SiteSpec(1, "pay", @"D:\pay", "Pool"));
            ids.Add(await t.Link(c, s, 1));
        }
        Assert.Equal([1, 2, 3], (await Task.WhenAll(ids.Select(Load))).Select(x => x.DeployOrder));
        Assert.True((await t.Targets.MoveAsync(t.Admin, ids[2], up: true)).Ok);
        Assert.Equal([1, 3, 2], (await Task.WhenAll(ids.Select(Load))).Select(x => x.DeployOrder));
        Assert.False((await t.Targets.MoveAsync(t.Admin, ids[0], up: true)).Ok);
        Assert.False((await t.Targets.MoveAsync(t.Admin, ids[1], up: false)).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.MoveAsync(t.Nobody, ids[0], true));
    }

    [Fact]
    public async Task Inactive_project_cannot_get_new_links()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        await t.Projects.SetActiveAsync(t.Admin, p, false);
        Assert.False((await t.Targets.LinkAsync(t.Admin, c, s, 1, "/")).Ok);
    }

    [Fact]
    public async Task Remove_frees_the_item_for_another_component_and_marks_the_site_unassigned()
    {
        var (p, c1, s) = await Basic(); var c2 = await t.NewComponent(p, "Other");
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c1, s, 1);
        Assert.True((await t.Targets.RemoveAsync(t.Admin, id)).Ok);
        await using (var db = t.Db.CreateDbContext()) Assert.Equal(MappingStatus.Unassigned, (await db.IisSites.SingleAsync()).MappingStatus);
        Assert.True((await t.Targets.LinkAsync(t.Admin, c2, s, 1, "/")).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.RemoveAsync(t.Nobody, id));
    }

    [Fact]
    public async Task Servers_with_targets_cannot_be_removed()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c, s, 1);
        Assert.False((await t.Servers.RemoveAsync(t.Admin, s)).Ok);
        await t.Targets.RemoveAsync(t.Admin, id);
        Assert.True((await t.Servers.RemoveAsync(t.Admin, s)).Ok);
    }

    // ---------- reconciliation ----------

    [Fact]
    public async Task Deleting_the_site_in_IIS_marks_the_link_Missing_and_logs_drift()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(2, "b", @"D:\b", "B"));            // site 1 deleted outside the portal
        var tg = await Load(id);
        Assert.Equal(TargetStatus.Missing, tg.Status);
        Assert.Contains("no longer exists", tg.StatusDetail);
        var (drift, _) = await t.Drift.ListAsync(t.Admin, null, true, null, 0, 50);
        Assert.Contains(drift, d => d.ObjectType == "Site" && d.ObjectRef == "a" && d.Kind == DriftKind.Deleted);
        Assert.Contains(drift, d => d.ObjectType == "Target" && d.Kind == DriftKind.Deleted);
        Assert.Contains("Drift.Detected:Success", await Audit());
    }

    [Fact]
    public async Task Renaming_the_site_in_IIS_marks_the_link_Missing()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(1, "a-renamed", @"D:\a", "A"));
        var tg = await Load(id);
        Assert.Equal(TargetStatus.Missing, tg.Status);
        Assert.Contains("renamed", tg.StatusDetail);
    }

    [Fact]
    public async Task Changing_folder_or_pool_marks_the_link_Drifted_and_it_can_be_accepted()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\Websites\a", "A"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(1, "a", @"D:\Websites\moved", "A2"));
        var tg = await Load(id);
        Assert.Equal(TargetStatus.Drifted, tg.Status);
        Assert.Equal(@"D:\Websites\a", tg.PhysicalPath);                 // the stored (approved) values are NOT overwritten silently

        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.AcceptCurrentAsync(t.ActorWith(Role.ServerOperator, "Dev"), id));
        Assert.True((await t.Targets.AcceptCurrentAsync(t.Admin, id)).Ok);
        tg = await Load(id);
        Assert.Equal((TargetStatus.Active, @"D:\Websites\moved", "A2"), (tg.Status, tg.PhysicalPath, tg.AppPoolName));
        var (open, _) = await t.Drift.ListAsync(t.Admin, null, true, "Target", 0, 50);
        Assert.Empty(open);       // the drift entries for this target were acknowledged
        await using var db = t.Db.CreateDbContext();
        var a = await db.AuditLog.SingleAsync(x => x.Action == "Target.AcceptDrift" && x.Result == "Success");
        Assert.Contains(@"D:\Websites\a", a.Before); Assert.Contains(@"D:\Websites\moved", a.After);
    }

    [Fact]
    public async Task Accepting_drift_re_checks_the_rules()
    {
        var (p, c1, s) = await Basic(); var c2 = await t.NewComponent(p, "Other");
        await t.Store(s, new SiteSpec(1, "a", @"D:\Websites\a", "A"), new SiteSpec(2, "b", @"D:\Websites\b", "B"));
        var t1 = await t.Link(c1, s, 1); await t.Link(c2, s, 2);
        await t.Store(s, new SiteSpec(1, "a", @"D:\Websites\b", "A"), new SiteSpec(2, "b", @"D:\Websites\b", "B"));   // a moved onto b's folder
        Assert.Equal(TargetStatus.Drifted, (await Load(t1)).Status);
        var r = await t.Targets.AcceptCurrentAsync(t.Admin, t1);
        Assert.False(r.Ok);
        Assert.Contains("exactly this folder", r.Error);
    }

    [Fact]
    public async Task Link_returns_to_Active_by_itself_when_IIS_matches_again()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(1, "a", @"D:\other", "A"));
        Assert.Equal(TargetStatus.Drifted, (await Load(id)).Status);
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));            // someone reverted the change
        var tg = await Load(id);
        Assert.Equal(TargetStatus.Active, tg.Status);
        Assert.Null(tg.StatusDetail);
    }

    [Fact]
    public async Task Missing_item_that_is_gone_cannot_be_accepted()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(2, "b", @"D:\b", "B"));
        var r = await t.Targets.AcceptCurrentAsync(t.Admin, id);
        Assert.False(r.Ok);
        Assert.Contains("no longer exists", r.Error);
        Assert.False((await t.Targets.AcceptCurrentAsync(t.Admin, await t.Link(await t.NewComponent(await t.NewProject("Other")), s, 2))).Ok);   // Active target: nothing to accept
    }

    [Fact]
    public async Task A_renamed_site_can_be_accepted_when_it_still_exists()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(1, "a-new", @"D:\a", "A"));
        Assert.True((await t.Targets.AcceptCurrentAsync(t.Admin, id)).Ok);
        var tg = await Load(id);
        Assert.Equal(("a-new", TargetStatus.Active), (tg.IisSiteName, tg.Status));
    }

    [Fact]
    public async Task First_discovery_never_creates_drift_and_no_data_never_marks_links_Missing()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var (drift, _) = await t.Drift.ListAsync(t.Admin, null, false, null, 0, 50);
        Assert.Empty(drift);
        Assert.Equal(0, await t.Reconciler.VerifyServerAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Site_state_changes_and_unchanged_snapshots_are_not_drift()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        Assert.Empty((await t.Drift.ListAsync(t.Admin, null, false, null, 0, 50)).Rows);
    }

    [Fact]
    public async Task Changes_outside_any_link_are_still_logged_as_drift()
    {
        var (_, _, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "new-site", @"D:\n", "N"));
        var (rows, _) = await t.Drift.ListAsync(t.Admin, null, true, null, 0, 50);
        Assert.Contains(rows, d => d.ObjectRef == "new-site" && d.Kind == DriftKind.Created);
        Assert.Contains(rows, d => d.ObjectType == "AppPool" && d.ObjectRef == "N" && d.Kind == DriftKind.Created);
    }

    [Fact]
    public async Task Disabled_targets_are_not_touched_by_reconciliation_and_can_be_re_enabled()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        var id = await t.Link(c, s, 1);
        await t.Targets.SetDisabledAsync(t.Admin, id, true);
        await t.Store(s, new SiteSpec(2, "b", @"D:\b", "B"));
        Assert.Equal(TargetStatus.Disabled, (await Load(id)).Status);
        await t.Targets.SetDisabledAsync(t.Admin, id, false);
        Assert.Equal(TargetStatus.Missing, (await Load(id)).Status);     // re-enabling re-checks against IIS
    }

    [Fact]
    public async Task Mapping_status_follows_the_links()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        await t.Link(c, s, 1);
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "b", @"D:\b", "B"));
        await using var db = t.Db.CreateDbContext();
        var m = await db.IisSites.ToDictionaryAsync(x => x.IisSiteId, x => x.MappingStatus);
        Assert.Equal((MappingStatus.Mapped, MappingStatus.Unassigned), (m[1], m[2]));
    }

    [Fact]
    public async Task Drift_list_is_scoped_by_environment_and_acknowledge_needs_permission()
    {
        var dev = await t.ApprovedServer("DEV1", "Dev"); var prd = await t.ApprovedServer("PRD1", "Prod");
        foreach (var s in new[] { dev, prd }) { await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A")); await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"), new SiteSpec(2, "x", @"D:\x", "X")); }
        var op = t.ActorWith(Role.ServerOperator, "Dev");
        var (rows, _) = await t.Drift.ListAsync(op, null, true, null, 0, 50);
        Assert.All(rows, r => Assert.Equal("Dev", r.Environment));
        Assert.NotEmpty(rows);
        var prodRow = (await t.Drift.ListAsync(t.Admin, prd, true, null, 0, 50)).Rows.First();
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Drift.AcknowledgeAsync(op, prodRow.Id));
        Assert.True((await t.Drift.AcknowledgeAsync(op, rows[0].Id)).Ok);
        Assert.True((await t.Drift.AcknowledgeAsync(t.Admin, prodRow.Id)).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Drift.AcknowledgeAsync(t.Nobody, prodRow.Id));
        var (open, blocked) = await t.Drift.CountsAsync(t.Admin);
        Assert.True(open >= 1); Assert.Equal(0, blocked);
    }

    [Fact]
    public async Task The_daily_job_re_checks_links_even_when_the_server_is_offline()
    {
        var (_, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var id = await t.Link(c, s, 1);
        await using (var db = t.Db.CreateDbContext())
        {   // simulate something that changed the stored link directly
            var tg = await db.DeploymentTargets.FirstAsync(); tg.PhysicalPath = @"D:\elsewhere"; await db.SaveChangesAsync();
        }
        var job = new ReconciliationJob(t.Db, t.Commands, t.Reconciler, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconciliationJob>.Instance);
        await job.RunAll();
        Assert.Equal(TargetStatus.Drifted, (await Load(id)).Status);
    }

    // ---------- onboarding list and suggestions ----------

    [Fact]
    public async Task Onboarding_list_shows_sites_and_sub_applications_with_mapping_state_and_suggestions_but_saves_nothing()
    {
        var p = await t.NewProject("Payments"); var c = await t.NewComponent(p, "Web Frontend");
        var s = await t.ApprovedServer();
        await t.Store(s, new SiteSpec(1, "payments-web-frontend-dev", @"D:\Websites\Payments\Web Frontend", "PayPool", ("/api", @"D:\Websites\Payments\api", "ApiPool")),
                         new SiteSpec(2, "legacy", @"D:\Websites\legacy", "LegacyPool"));
        await t.Link(c, s, 1);
        var (rows, total) = await t.Targets.ListCandidatesAsync(t.Admin, null, null, false, 0, 50);
        Assert.Equal(3, total);
        Assert.True(rows.Single(r => r.SiteName.StartsWith("payments") && r.AppPath == "/").IsMapped);
        var api = rows.Single(r => r.AppPath == "/api");
        Assert.False(api.IsMapped);
        Assert.Equal("Payments", api.Suggestion.ProjectName);
        Assert.Equal(Confidence.None, rows.Single(r => r.SiteName == "legacy").Suggestion.Confidence);
        Assert.Equal(2, (await t.Targets.ListCandidatesAsync(t.Admin, null, null, true, 0, 50)).Total);
        Assert.Equal(1, (await t.Targets.ListCandidatesAsync(t.Admin, null, "legacy", true, 0, 50)).Total);
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(1, await db.DeploymentTargets.CountAsync());     // suggestions alone never create a mapping
    }

    [Fact]
    public async Task Onboarding_list_needs_admin_or_owner_and_owners_only_get_suggestions_for_their_projects()
    {
        var pay = await t.NewProject("Payments"); var inv = await t.NewProject("Inventory");
        await t.NewComponent(pay); await t.NewComponent(inv, "Web");
        var s = await t.ApprovedServer();
        await t.Store(s, new SiteSpec(1, "inventory-web-dev", @"D:\Websites\Inventory\Web", "P"));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.ListCandidatesAsync(t.Nobody, null, null, false, 0, 10));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.ListCandidatesAsync(t.ActorWith(Role.ServerOperator, "Dev"), null, null, false, 0, 10));
        var rows = (await t.Targets.ListCandidatesAsync(Owner(pay), null, null, false, 0, 10)).Rows;
        Assert.Equal(Confidence.None, rows.Single().Suggestion.Confidence);      // Inventory is not theirs
        Assert.Equal("Inventory", (await t.Targets.ListCandidatesAsync(t.Admin, null, null, false, 0, 10)).Rows.Single().Suggestion.ProjectName);
    }

    [Fact]
    public async Task Preview_reports_violations_without_changing_anything()
    {
        var p1 = await t.NewProject("Payments"); var p2 = await t.NewProject("Inventory");
        var c1 = await t.NewComponent(p1); var c2 = await t.NewComponent(p2, "Web");
        var s = await t.ApprovedServer();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "Shared"), new SiteSpec(2, "b", @"D:\b", "Shared"));
        await t.Link(c1, s, 1);
        var pv = (await t.Targets.PreviewAsync(t.Admin, c2, s, 2, "/")).Value!;
        Assert.True(pv.NeedsException); Assert.False(pv.HasBlocking); Assert.False(pv.CanLink);
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(1, await db.DeploymentTargets.CountAsync());
    }

    // ---------- link requests ----------

    [Fact]
    public async Task Owner_requests_and_admin_approves_which_creates_the_link_with_the_requester_recorded()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var owner = Owner(p);
        var req = await t.Targets.RequestLinkAsync(owner, c, s, 1, "/", "Needed for go-live");
        Assert.True(req.Ok, req.Error);
        Assert.Single(await t.Targets.ListRequestsAsync(owner, true));
        Assert.Single(await t.Targets.ListRequestsAsync(t.Admin, true));
        Assert.Empty(await t.Targets.ListRequestsAsync(Owner(await t.NewProject("Elsewhere")), true));   // another project's owner cannot see it

        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.DecideAsync(owner, req.Value, true, null));    // owner cannot approve own request
        Assert.True((await t.Targets.DecideAsync(t.Admin, req.Value, true, "ok")).Ok);
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(1, await db.DeploymentTargets.CountAsync());
        Assert.Equal(LinkRequestStatus.Approved, (await db.LinkRequests.SingleAsync()).Status);
        Assert.False((await t.Targets.DecideAsync(t.Admin, req.Value, true, null)).Ok);   // already decided
    }

    [Fact]
    public async Task Rejecting_needs_a_comment_and_creates_nothing()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var req = await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", "why");
        Assert.False((await t.Targets.DecideAsync(t.Admin, req.Value, false, " ")).Ok);
        Assert.True((await t.Targets.DecideAsync(t.Admin, req.Value, false, "Wrong site")).Ok);
        await using var db = t.Db.CreateDbContext();
        Assert.Equal(0, await db.DeploymentTargets.CountAsync());
        Assert.Equal("Wrong site", (await db.LinkRequests.SingleAsync()).DecisionComment);
    }

    [Fact]
    public async Task Request_rules()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.RequestLinkAsync(t.Nobody, c, s, 1, "/", "x"));
        var otherOwner = Owner(await t.NewProject("Other1"));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.RequestLinkAsync(otherOwner, c, s, 1, "/", "x"));
        Assert.False((await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", " ")).Ok);                // reason required
        Assert.True((await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", "x")).Ok);
        Assert.False((await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", "again")).Ok);              // duplicate pending
    }

    [Fact]
    public async Task Requester_can_cancel_and_nobody_else_can()
    {
        var (p, c, s) = await Basic();
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var req = await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", "x");
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Targets.CancelRequestAsync(t.Nobody, req.Value));
        Assert.True((await t.Targets.CancelRequestAsync(Owner(p), req.Value)).Ok);
        Assert.Empty(await t.Targets.ListRequestsAsync(t.Admin, true));
    }

    [Fact]
    public async Task Approval_that_would_break_a_rule_fails_and_leaves_the_request_pending()
    {
        var (p, c, s) = await Basic(); var c2 = await t.NewComponent(p, "Other");
        await t.Store(s, new SiteSpec(1, "a", @"D:\a", "A"));
        var req = await t.Targets.RequestLinkAsync(Owner(p), c, s, 1, "/", "x");
        await t.Link(c2, s, 1);                                      // someone linked the same site meanwhile
        Assert.False((await t.Targets.DecideAsync(t.Admin, req.Value, true, null)).Ok);
        Assert.Single(await t.Targets.ListRequestsAsync(t.Admin, true));
    }
}

public class CertificateRenewalTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    private async Task<List<AgentCertificate>> Certs(Guid id) { await using var db = t.Db.CreateDbContext(); return await db.AgentCertificates.Where(c => c.ServerId == id).OrderBy(c => c.CreatedAt).ToListAsync(); }

    [Fact]
    public async Task Approved_agent_can_renew_and_the_old_certificate_keeps_working_until_the_new_one_is_used()
    {
        var id = await t.ApprovedServer();
        var (_, oldThumb) = (0, (await Certs(id)).Single().Thumbprint);
        t.Clock.Advance(TimeSpan.FromDays(640));
        var fresh = t.NewCert();
        var r = await t.Servers.RenewCertificateAsync(id, oldThumb, fresh.Base64);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(fresh.Thumbprint, r.Value);
        Assert.True(await t.Servers.IsCertificateActiveAsync(id, oldThumb));          // still valid: a crash now must not lock the agent out
        Assert.True(await t.Servers.IsCertificateActiveAsync(id, fresh.Thumbprint));

        await t.Servers.SupersedeOlderCertificatesAsync(id, fresh.Thumbprint);        // the agent connected with the new one
        Assert.False(await t.Servers.IsCertificateActiveAsync(id, oldThumb));
        Assert.True(await t.Servers.IsCertificateActiveAsync(id, fresh.Thumbprint));
        Assert.Equal([CertificateStatus.Superseded, CertificateStatus.Active], (await Certs(id)).Select(c => c.Status));
    }

    [Fact]
    public async Task Connecting_with_the_old_certificate_does_not_retire_anything()
    {
        var id = await t.ApprovedServer();
        var old = (await Certs(id)).Single().Thumbprint;
        t.Clock.Advance(TimeSpan.FromDays(1));
        await t.Servers.RenewCertificateAsync(id, old, t.NewCert().Base64);
        await t.Servers.SupersedeOlderCertificatesAsync(id, old);
        Assert.All(await Certs(id), c => Assert.Equal(CertificateStatus.Active, c.Status));
    }

    [Fact]
    public async Task Pending_revoked_and_unknown_servers_cannot_renew()
    {
        var (pending, th) = await t.RegisteredServer("PEND");
        Assert.False((await t.Servers.RenewCertificateAsync(pending, th, t.NewCert().Base64)).Ok);
        var id = await t.ApprovedServer("OK1");
        await t.Servers.RevokeAsync(t.Admin, id, "x");
        Assert.False((await t.Servers.RenewCertificateAsync(id, "x", t.NewCert().Base64)).Ok);
        Assert.False((await t.Servers.RenewCertificateAsync(Guid.NewGuid(), "x", t.NewCert().Base64)).Ok);
    }

    [Fact]
    public async Task Renewal_certificates_are_validated_like_registration_ones_and_cannot_pile_up()
    {
        var id = await t.ApprovedServer();
        var old = (await Certs(id)).Single().Thumbprint;
        Assert.False((await t.Servers.RenewCertificateAsync(id, old, t.NewCert(keyBits: 1024).Base64)).Ok);
        Assert.False((await t.Servers.RenewCertificateAsync(id, old, "not a certificate")).Ok);
        Assert.False((await t.Servers.RenewCertificateAsync(id, old, Convert.ToBase64String(t.NewCert().Cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx)))).Ok);
        var first = t.NewCert();
        Assert.True((await t.Servers.RenewCertificateAsync(id, old, first.Base64)).Ok);
        Assert.False((await t.Servers.RenewCertificateAsync(id, old, first.Base64)).Ok);       // same certificate twice
        Assert.False((await t.Servers.RenewCertificateAsync(id, old, t.NewCert().Base64)).Ok); // a second pending renewal
    }

    [Fact]
    public async Task Renewal_is_audited_and_the_server_page_can_list_certificates_with_days_left()
    {
        var id = await t.ApprovedServer();
        t.Clock.Advance(TimeSpan.FromDays(10));
        await t.Servers.RenewCertificateAsync(id, (await Certs(id)).Single().Thumbprint, t.NewCert().Base64);
        await using (var db = t.Db.CreateDbContext()) Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Agent.CertRenewed");
        var rows = await t.Queries.GetAgentCertificatesAsync(t.Admin, id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.InRange(r.DaysLeft, 600, 700));
    }
}
