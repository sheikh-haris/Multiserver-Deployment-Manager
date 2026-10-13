using IisControlCenter.Contracts;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;

namespace IisControlCenter.Web.Tests;

public class PromoteTests : IDisposable
{
    private readonly DeployEnv d = new();
    public void Dispose() => d.Dispose();
    private static (string, string)[] Site(string v = "1") => [("index.html", "v" + v), ("app.dll", "dll" + v)];

    /// <summary>Staging server with a target, plus a Prod server with a target for the same component.</summary>
    private async Task<(Actor stagingDep, Actor stagingAppr, Actor prodDep, Actor prodAppr, Guid prodTarget)> World()
    {
        await d.SetUp("Staging");
        var (_, pt) = await d.AddServer("PROD01", "Prod", 21);
        return (d.Deployer("Staging"), d.Approver("Staging"), d.Person("pdep", (Role.Deployer, "Prod")), d.Person("papp", (Role.Approver, "Prod")), pt);
    }

    [Fact]
    public async Task A_package_that_succeeded_on_staging_is_offered_for_promotion_to_people_who_can_deploy_to_production_only()
    {
        var (sdep, sappr, pdep, _, _) = await World();
        var pkg = await d.Upload(sdep, "Staging", Site());
        var req = await d.SubmitApprovedAndRun(sdep, sappr, pkg, "Staging");
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(req)).Status);

        var row = Assert.Single(await d.Svc.PromotableAsync(pdep));
        Assert.Equal((pkg.Sha256, null), (row.Sha256, row.RunningInProd));
        Assert.Empty(await d.Svc.PromotableAsync(sdep));                                        // only a Staging deployer: cannot deploy to Prod
        Assert.Empty(await d.Svc.PromotableAsync(d.E.Nobody));
    }

    [Fact]
    public async Task It_disappears_once_a_request_is_open_or_the_package_runs_in_production()
    {
        var (sdep, sappr, pdep, papp, pt) = await World();
        var pkg = await d.Upload(sdep, "Staging", Site());
        await d.SubmitApprovedAndRun(sdep, sappr, pkg, "Staging");
        var pkgProd = await d.Svc.PackagesAsync(pdep, d.ComponentId, "Prod");
        var promote = await d.Svc.SubmitAsync(pdep, d.Cmd(pkg, "Prod", targets: [pt]));
        Assert.True(promote.Ok, promote.Error);
        Assert.Empty(await d.Svc.PromotableAsync(pdep));                                        // open request

        await d.Svc.ApproveAsync(papp, promote.Value);
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(promote.Value)).Status);
        Assert.Empty(await d.Svc.PromotableAsync(pdep));                                        // it is in production now
        _ = pkgProd;
    }

    [Fact]
    public async Task A_newer_staging_package_shows_what_production_runs_now()
    {
        var (sdep, sappr, pdep, papp, pt) = await World();
        var v1 = await d.Upload(sdep, "Staging", Site("1"));
        await d.SubmitApprovedAndRun(sdep, sappr, v1, "Staging");
        var p1 = await d.Svc.SubmitAsync(pdep, d.Cmd(v1, "Prod", targets: [pt])); await d.Svc.ApproveAsync(papp, p1.Value); await d.Orch.TickAsync();

        d.E.Clock.Advance(TimeSpan.FromMinutes(5));
        var v2 = await d.Upload(sdep, "Staging", Site("2"), "v2.zip");
        await d.SubmitApprovedAndRun(sdep, sappr, v2, "Staging");
        var row = Assert.Single(await d.Svc.PromotableAsync(pdep));
        Assert.Equal(v2.Sha256, row.Sha256);
        Assert.Equal("build-1.zip", row.RunningInProd);
    }

    [Fact]
    public async Task Components_without_an_active_production_target_are_not_offered()
    {
        await d.SetUp("Staging");
        var sdep = d.Deployer("Staging"); var sappr = d.Approver("Staging");
        var pdep = d.Person("pdep", (Role.Deployer, "Prod"));
        var pkg = await d.Upload(sdep, "Staging", Site());
        await d.SubmitApprovedAndRun(sdep, sappr, pkg, "Staging");
        Assert.Empty(await d.Svc.PromotableAsync(pdep));
    }

    [Fact]
    public async Task Versions_show_the_current_package_per_environment_and_fall_back_after_a_manual_rollback()
    {
        var (sdep, sappr, _, _, _) = await World();
        var viewer = d.E.ActorWith(Role.PlatformAdmin);
        var v1 = await d.Upload(sdep, "Staging", Site("1"));
        var r1 = await d.SubmitApprovedAndRun(sdep, sappr, v1, "Staging");
        d.E.Clock.Advance(TimeSpan.FromMinutes(5));
        var v2 = await d.Upload(sdep, "Staging", Site("2"), "v2.zip");
        var r2 = await d.SubmitApprovedAndRun(sdep, sappr, v2, "Staging");

        var row = Assert.Single(await d.Svc.VersionsAsync(viewer, d.ProjectId));
        Assert.Equal("v2.zip", row.Cells["Staging"]!.PackageName);
        Assert.Null(row.Cells["Dev"]); Assert.Null(row.Cells["Prod"]);
        Assert.Contains("Staging", row.EnvironmentsWithTargets); Assert.Contains("Prod", row.EnvironmentsWithTargets);

        var run2 = (await d.Runs(r2)).Single().Id;
        var rb = await d.Svc.RequestRollbackAsync(sdep, run2, "bad");
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(rb.Value)).Status);
        var after = Assert.Single(await d.Svc.VersionsAsync(viewer, d.ProjectId));
        Assert.Equal("build-1.zip", after.Cells["Staging"]!.PackageName);                       // v2 was undone
        _ = r1;
    }

    [Fact]
    public async Task Versions_hide_environments_the_person_cannot_see()
    {
        var (sdep, sappr, _, _, _) = await World();
        var pkg = await d.Upload(sdep, "Staging", Site());
        await d.SubmitApprovedAndRun(sdep, sappr, pkg, "Staging");
        var row = Assert.Single(await d.Svc.VersionsAsync(d.Person("viewer", (Role.ProjectViewer, "Staging")), d.ProjectId));
        Assert.True(row.Cells.ContainsKey("Staging")); Assert.False(row.Cells.ContainsKey("Prod")); Assert.False(row.Cells.ContainsKey("Dev"));
    }
}
