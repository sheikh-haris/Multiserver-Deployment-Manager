using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public class ReportTests : IDisposable
{
    private readonly DeployEnv d = new();
    private readonly ReportService reports;
    public void Dispose() => d.Dispose();
    public ReportTests() => reports = new ReportService(d.E.Db, d.E.Audit, d.E.Clock);

    private static (string, string)[] Site() => [("index.html", "v1")];
    private static readonly DateTimeOffset Far = DateTimeOffset.UtcNow.AddYears(5);

    [Fact]
    public async Task Deployment_history_lists_requests_with_approvers_and_flags_and_is_scoped_to_what_the_person_may_see()
    {
        await d.SetUp("Dev");
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");

        var admin = await reports.DeploymentHistoryCsvAsync(d.E.Admin, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1));
        var lines = admin.TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"Succeeded\"", lines[1]); Assert.Contains("User app", lines[1]); Assert.Contains("\"PR-1\"", lines[1]); Assert.Contains("\"no\",\"no\"", lines[1]);

        Assert.Equal(2, (await reports.DeploymentHistoryCsvAsync(dep, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1))).TrimEnd().Split('\n').Length);
        var stranger = await reports.DeploymentHistoryCsvAsync(d.E.Nobody, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1));
        Assert.Single(stranger.TrimEnd().Split('\n'));                                          // header only
        var otherEnv = await reports.DeploymentHistoryCsvAsync(d.Person("v", (Role.ProjectViewer, "Prod")), d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1));
        Assert.Single(otherEnv.TrimEnd().Split('\n'));
        var filtered = await reports.DeploymentHistoryCsvAsync(d.E.Admin, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1), environment: "Prod");
        Assert.Single(filtered.TrimEnd().Split('\n'));
    }

    [Fact]
    public async Task Exports_neutralise_spreadsheet_formulas()
    {
        await d.SetUp("Dev");
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var cmd = d.Cmd(pkg, "Dev", source: "=HYPERLINK(\"http://evil\")");
        await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev", cmd);
        var csv = await reports.DeploymentHistoryCsvAsync(d.E.Admin, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1));
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.DoesNotContain(",\"=HYPERLINK", csv);
    }

    [Fact]
    public async Task Inventory_lists_every_application_with_pool_and_bindings_and_needs_the_server_view_permission()
    {
        await d.SetUp("Dev");
        var csv = await reports.InventoryCsvAsync(d.E.Admin);
        Assert.Contains("WEB01-site", csv); Assert.Contains("WEB01Pool", csv); Assert.Contains("\"Dev\"", csv);
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.InventoryCsvAsync(d.E.Nobody));
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.InventoryCsvAsync(d.Deployer("Dev")));
        var prodOnly = await reports.InventoryCsvAsync(d.E.ActorWith(Role.ServerOperator, "Prod", "op"));
        Assert.DoesNotContain("WEB01-site", prodOnly);                                          // a Dev server is not visible to a Prod operator
    }

    [Fact]
    public async Task The_access_report_shows_roles_with_expiry_and_status_and_is_for_auditors_and_admins()
    {
        await d.SetUp("Dev");
        d.Person("alice", (Role.Deployer, "Dev"));
        await using (var db = d.E.Db.CreateDbContext())
        {
            db.RoleAssignments.Add(new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "bob", PrincipalName = "bob", Scope = RoleScope.Project, ProjectId = d.ProjectId, Role = Role.Approver, EnvironmentName = "Dev", GrantedBy = "x", GrantedAt = d.E.Clock.GetUtcNow(), ExpiresAt = d.E.Clock.GetUtcNow().AddDays(5) });
            db.RoleAssignments.Add(new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "old", PrincipalName = "old", Scope = RoleScope.Project, ProjectId = d.ProjectId, Role = Role.Deployer, EnvironmentName = "Dev", GrantedBy = "x", GrantedAt = d.E.Clock.GetUtcNow(), RevokedAt = d.E.Clock.GetUtcNow(), RevokedBy = "y" });
            await db.SaveChangesAsync();
        }
        var csv = await reports.AccessCsvAsync(d.E.ActorWith(Role.Auditor));
        Assert.Contains("\"alice\"", csv); Assert.Contains("\"Expires soon\"", csv); Assert.DoesNotContain("\"old\"", csv);
        Assert.Contains("\"old\"", await reports.AccessCsvAsync(d.E.Admin, includeRevoked: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.AccessCsvAsync(d.Deployer("Dev")));
        await Assert.ThrowsAsync<ForbiddenException>(() => reports.AccessCsvAsync(d.E.Nobody));
    }

    [Fact]
    public async Task Every_export_is_audited()
    {
        await d.SetUp("Dev");
        await reports.InventoryCsvAsync(d.E.Admin); await reports.AccessCsvAsync(d.E.Admin);
        await reports.DeploymentHistoryCsvAsync(d.E.Admin, d.E.Clock.GetUtcNow().AddDays(-1), d.E.Clock.GetUtcNow().AddDays(1));
        Assert.Equal(3, (await d.AuditActions()).Count(a => a == "Report.Export:Success"));
    }
}
