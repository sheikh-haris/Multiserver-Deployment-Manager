using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class OrchestratorTests : IDisposable
{
    private readonly DeployEnv d = new();
    public void Dispose() => d.Dispose();
    private static (string, string)[] Site(string v = "1") => [("index.html", "v" + v), ("app.dll", "dll" + v)];

    private async Task<(Actor dep, Actor appr, Package pkg)> Ready(string env = "Dev")
    {
        await d.SetUp(env);
        var dep = d.Deployer(env); var appr = d.Approver(env);
        return (dep, appr, await d.Upload(dep, env, Site()));
    }

    // ---------------- happy path ----------------

    [Fact]
    public async Task Approved_request_is_sent_to_the_agent_with_the_exact_approved_settings_and_succeeds()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var (env, cmd) = Assert.Single(d.E.Hub.Client.Deploys);
        Assert.Equal((d.ServerId, "Dev"), (env.ServerId, env.Environment));
        Assert.Equal((pkg.Id, pkg.Sha256, pkg.Size, @"D:\Websites\WEB01", 11L, "/"), (cmd.PackageId, cmd.PackageSha256, cmd.PackageSize, cmd.PhysicalPath, cmd.IisSiteId, cmd.AppPath));
        Assert.Contains("web.config", cmd.ProtectedConfig); Assert.Contains("uploads", cmd.ProtectedData.Select(x => x.TrimEnd('\\', '/')));
        Assert.Equal("/health", cmd.HealthPath);
        Assert.False(cmd.ScanOnAgent);
        var req = await d.Req(id);
        Assert.Equal(RequestStatus.Succeeded, req.Status);
        Assert.NotNull(req.StartedAt); Assert.NotNull(req.EndedAt);
        var run = Assert.Single(await d.Runs(id));
        Assert.Equal((RunStatus.Succeeded, 2, 3), (run.Status, run.Added, run.Replaced));
        Assert.NotNull(run.BackupId);
        Assert.Contains("Deployment.Run.Start:Success", await d.AuditActions());
        Assert.Contains("Deployment.Run.Result:Success", await d.AuditActions());
        Assert.Contains("Deployment.Finish:Success", await d.AuditActions());
    }

    [Fact]
    public async Task Agent_log_lines_are_stored_in_order()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var run = (await d.Runs(id)).Single();
        var log = await d.Svc.GetLogAsync(dep, run.Id);
        Assert.Equal("starting", Assert.Single(log).Message);
        Assert.Empty(await d.Svc.GetLogAsync(d.E.Nobody, run.Id));       // not visible without a role
    }

    [Fact]
    public async Task Scan_on_agent_flag_follows_the_package()
    {
        var (dep, appr, _) = await Ready();
        d.Av.Result = new(ScanOutcome.Unavailable, "no defender");
        var pkg = await d.Upload(dep, "Dev", Site("9"), "b2.zip");
        Assert.Equal(AvResult.ScanOnTarget, pkg.AvResult);
        await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        Assert.True(d.E.Hub.Client.Deploys.Single().Command.ScanOnAgent);
    }

    // ---------------- sub-applications in sub-folders ----------------

    [Fact]
    public async Task A_sub_application_inside_the_targets_folder_is_protected_in_the_review_and_in_the_command()
    {
        var (dep, appr, pkg0) = await Ready();
        await d.E.Store(d.ServerId, new TestEnv.SiteSpec(11, "WEB01-site", @"D:\Websites\WEB01", "WEB01Pool", ("/api", @"D:\Websites\WEB01\api", "ApiPool")));
        d.Live["api/Api.dll"] = "child"; d.Live["old.txt"] = "x";
        d.E.Hub.Client.Deploys.Clear();
        var zip = await d.Upload(dep, "Dev", [("index.html", "v2"), ("api/Api.dll", "WRONG")], "b3.zip");
        await d.Protected.ConfirmAsync(d.E.Admin, d.ComponentId);
        var id = await d.SubmitApprovedAndRun(dep, appr, zip, "Dev", d.Cmd(zip, "Dev", mode: DeployMode.FullReplace));

        var changes = d.Store.ReadChanges(id, d.TargetId);
        Assert.Contains(changes, c => c.Path == "api/Api.dll" && c.Action == FileAction.SkipProtectedData);
        Assert.DoesNotContain(changes, c => c.Action == FileAction.Delete && c.Path.StartsWith("api/"));
        Assert.Contains(d.E.Hub.Client.Deploys.Single().Command.ProtectedData, f => f.TrimEnd('/', '\\') == "api");
    }

    // ---------------- failures ----------------

    [Fact]
    public async Task Failed_run_with_a_rollback_gives_RolledBack_and_a_failed_run_gives_Failed()
    {
        var (dep, appr, pkg) = await Ready();
        d.DeployOutcome = c => new(c.RunId, RunResults.RolledBack, "bk1", null, "Health", "health check failed", 1, 1, 0, 0, 900, "GET / -> 500");
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        Assert.Equal(RequestStatus.RolledBack, (await d.Req(id)).Status);
        Assert.Equal(RunStatus.RolledBack, (await d.Runs(id)).Single().Status);

        d.DeployOutcome = c => new(c.RunId, RunResults.Failed, null, null, "Copy", "disk full", 0, 0, 0, 0, 100, null);
        var id2 = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        Assert.Equal(RequestStatus.Failed, (await d.Req(id2)).Status);
        Assert.Equal("Copy", (await d.Runs(id2)).Single().FailureStep);
    }

    [Fact]
    public async Task Rollback_failed_is_marked_loudly()
    {
        var (dep, appr, pkg) = await Ready();
        d.DeployOutcome = c => new(c.RunId, RunResults.RollbackFailed, "bk", null, "Health", "could not restore", 0, 0, 0, 0, 10, null);
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var run = (await d.Runs(id)).Single();
        Assert.Equal(RequestStatus.Failed, (await d.Req(id)).Status);
        Assert.StartsWith("ROLLBACK FAILED", run.Message);
    }

    [Fact]
    public async Task Folder_changed_since_approval_sends_the_request_back_for_approval()
    {
        var (dep, appr, pkg) = await Ready();
        d.DeployOutcome = c => new(c.RunId, RunResults.NeedsReapproval, null, null, "Snapshot", "The folder changed after the request was approved", 0, 0, 0, 0, 5, null);
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        Assert.Equal(RequestStatus.NeedsReapproval, (await d.Req(id)).Status);
        Assert.Equal(RunStatus.NeedsReapproval, (await d.Runs(id)).Single().Status);
    }

    [Fact]
    public async Task Targets_run_one_at_a_time_in_order_and_stop_at_the_first_failure_with_a_partial_banner()
    {
        var (dep, appr, pkg) = await Ready();
        var (s2, t2) = await d.AddServer("WEB02", "Dev", 12);
        var (s3, t3) = await d.AddServer("WEB03", "Dev", 13);
        var cmd = d.Cmd(pkg, "Dev", targets: [d.TargetId, t2, t3]);
        var failOn = s2;
        d.DeployOutcome = c => c.TargetId == t2
            ? new(c.RunId, RunResults.Failed, null, null, "Copy", "disk full", 0, 0, 0, 0, 1, null)
            : DeployEnv.Success(c);
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", cmd);
        await d.Orch.TickAsync();

        Assert.Equal([d.TargetId, t2], d.E.Hub.Client.Deploys.Select(x => x.Command.TargetId));      // third never started
        Assert.Equal(RequestStatus.Failed, (await d.Req(id)).Status);
        var det = await d.Svc.GetAsync(dep, id);
        Assert.Contains("Partly deployed", det!.PartialBanner);
        Assert.Contains("WEB01", det.PartialBanner); Assert.Contains("WEB03", det.PartialBanner);
        _ = failOn; _ = s3; _ = t3;
    }

    [Fact]
    public async Task All_targets_succeeding_ends_Succeeded_in_target_order()
    {
        var (dep, appr, pkg) = await Ready();
        var (_, t2) = await d.AddServer("WEB02", "Dev", 12);
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", targets: [t2, d.TargetId]));
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(id)).Status);
        Assert.Equal(2, d.E.Hub.Client.Deploys.Count);
        Assert.Equal([RunStatus.Succeeded, RunStatus.Succeeded], (await d.Runs(id)).Select(r => r.Status));
    }

    // ---------------- waiting, timeouts, schedule ----------------

    [Fact]
    public async Task Offline_agent_keeps_the_request_Approved_and_it_starts_when_the_agent_returns()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev");
        d.E.Tracker.Remove(d.ServerId, "conn-WEB01");
        await d.Orch.TickAsync();
        var r = await d.Req(id);
        Assert.Equal(RequestStatus.Approved, r.Status);
        Assert.Contains("offline", r.StatusDetail);
        Assert.Empty(d.E.Hub.Client.Deploys);
        d.E.Tracker.Add(d.ServerId, "conn-again");
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task Run_with_no_result_times_out_and_a_late_result_is_still_recorded()
    {
        var (dep, appr, pkg) = await Ready();
        d.AutoRunDeploy = false;
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        Assert.Equal(RequestStatus.Running, (await d.Req(id)).Status);
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Running, (await d.Req(id)).Status);          // still within the limit
        d.E.Clock.Advance(TimeSpan.FromMinutes(31));
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Failed, (await d.Req(id)).Status);
        Assert.Equal("Timeout", (await d.Runs(id)).Single().FailureStep);

        // the agent finishes after all
        var run = (await d.Runs(id)).Single();
        Assert.True(await d.Reports.CompletedAsync(d.ServerId, DeployEnv.Success(d.E.Hub.Client.Deploys.Single().Command)));
        Assert.Equal(RunStatus.Succeeded, (await d.Runs(id)).Single().Status);
        _ = run;
    }

    [Fact]
    public async Task Agent_going_offline_mid_request_gets_a_grace_period_then_the_rest_is_failed()
    {
        var (dep, appr, pkg) = await Ready();
        var (s2, t2) = await d.AddServer("WEB02", "Dev", 12);
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", targets: [d.TargetId, t2]));
        d.E.Tracker.Remove(s2, "conn-WEB02");
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Running, (await d.Req(id)).Status);
        d.E.Clock.Advance(TimeSpan.FromMinutes(5));
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Running, (await d.Req(id)).Status);
        d.E.Clock.Advance(TimeSpan.FromMinutes(6));
        await d.Orch.TickAsync();
        var r = await d.Req(id);
        Assert.Equal(RequestStatus.Failed, r.Status);
        Assert.Contains("offline", r.StatusDetail);
        Assert.Contains("Partly deployed", (await d.Svc.GetAsync(dep, id))!.PartialBanner);
    }

    [Fact]
    public async Task Scheduled_request_waits_for_its_time()
    {
        var (dep, appr, pkg) = await Ready();
        var at = d.E.Clock.GetUtcNow().AddHours(2);
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", at: at));
        Assert.Equal(RequestStatus.Scheduled, (await d.Req(id)).Status);
        await d.Orch.TickAsync();
        Assert.Empty(d.E.Hub.Client.Deploys);
        d.E.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task Rescheduling_changes_the_status_and_is_audited()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev");
        var at = d.E.Clock.GetUtcNow().AddHours(1);
        Assert.True((await d.Svc.RescheduleAsync(dep, id, at)).Ok);
        Assert.Equal(RequestStatus.Scheduled, (await d.Req(id)).Status);
        Assert.True((await d.Svc.RescheduleAsync(dep, id, null)).Ok);
        Assert.Equal(RequestStatus.Approved, (await d.Req(id)).Status);
        Assert.False((await d.Svc.RescheduleAsync(dep, id, d.E.Clock.GetUtcNow().AddDays(9))).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.RescheduleAsync(d.Person("z", (Role.Deployer, "Dev")), id, at));
        Assert.Contains("Deployment.Reschedule:Success", await d.AuditActions());
    }

    // ---------------- permission re-check at execution time ----------------

    [Fact]
    public async Task Removing_the_approvers_role_before_execution_sends_the_request_back_for_approval()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", at: d.E.Clock.GetUtcNow().AddHours(1)));
        await d.RevokeAllFor("app");
        d.E.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await d.Orch.TickAsync();
        var r = await d.Req(id);
        Assert.Equal(RequestStatus.NeedsReapproval, r.Status);
        Assert.Contains("approver", r.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(d.E.Hub.Client.Deploys);
        Assert.Contains("Deployment.NeedsReapproval:Success", await d.AuditActions());
    }

    [Fact]
    public async Task Removing_the_requesters_role_before_execution_stops_the_request()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", at: d.E.Clock.GetUtcNow().AddHours(1)));
        await d.RevokeAllFor("dep");
        d.E.Clock.Advance(TimeSpan.FromHours(2));
        await d.Orch.TickAsync();
        Assert.Equal(RequestStatus.NeedsReapproval, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task A_moved_iis_folder_or_drifted_target_stops_the_request()
    {
        var (dep, appr, pkg) = await Ready();
        var id = await d.SubmitApproved(dep, appr, pkg, "Dev", d.Cmd(pkg, "Dev", at: d.E.Clock.GetUtcNow().AddHours(1)));
        await using (var db = d.E.Db.CreateDbContext()) { (await db.DeploymentTargets.FirstAsync()).PhysicalPath = @"D:\Websites\elsewhere"; await db.SaveChangesAsync(); }
        d.E.Clock.Advance(TimeSpan.FromHours(2));
        await d.Orch.TickAsync();
        var r = await d.Req(id);
        Assert.Equal(RequestStatus.NeedsReapproval, r.Status);
        Assert.Contains("folder", r.StatusDetail);
    }

    // ---------------- result handling ----------------

    [Fact]
    public async Task Result_from_another_servers_agent_is_ignored_and_a_repeat_changes_nothing()
    {
        var (dep, appr, pkg) = await Ready();
        d.AutoRunDeploy = false;
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var cmd = d.E.Hub.Client.Deploys.Single().Command;
        var (other, _) = await d.AddServer("WEB02", "Dev", 12);
        await d.Reports.CompletedAsync(other, DeployEnv.Success(cmd));                          // wrong server
        Assert.Equal(RequestStatus.Running, (await d.Req(id)).Status);

        Assert.True(await d.Reports.CompletedAsync(d.ServerId, DeployEnv.Success(cmd)));
        var after = await d.Runs(id);
        Assert.True(await d.Reports.CompletedAsync(d.ServerId, DeployEnv.Success(cmd) with { Added = 99 }));   // repeat
        Assert.Equal(after[0].Added, (await d.Runs(id))[0].Added);
        Assert.Single(await d.AuditActions().ContinueWith(t => t.Result.Where(a => a == "Deployment.Run.Result:Success")));
    }

    [Fact]
    public async Task Unknown_run_results_are_acknowledged_but_stored_nowhere()
    {
        await d.SetUp();
        Assert.True(await d.Reports.CompletedAsync(d.ServerId, new RunCompleted(Guid.NewGuid(), RunResults.Succeeded, null, null, null, null, 0, 0, 0, 0, 1, null)));
    }

    [Fact]
    public async Task Log_storage_is_capped_per_run()
    {
        var (dep, appr, pkg) = await Ready();
        d.AutoRunDeploy = false;
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var run = (await d.Runs(id)).Single();
        var lines = Enumerable.Range(0, 500).Select(i => new RunLogLine(d.E.Clock.GetUtcNow(), "Info", "Copy", "line " + i)).ToList();
        for (var i = 0; i < 12; i++) await d.Reports.LogAsync(d.ServerId, new RunLog(run.Id, lines));
        await using var db = d.E.Db.CreateDbContext();
        Assert.Equal(RunReportService.MaxLogLinesPerRun, await db.DeploymentLogLines.CountAsync());
    }

    [Fact]
    public async Task Step_report_updates_the_current_step_only_while_running()
    {
        var (dep, appr, pkg) = await Ready();
        d.AutoRunDeploy = false;
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, "Dev");
        var run = (await d.Runs(id)).Single();
        await d.Reports.StepAsync(d.ServerId, new RunStep(run.Id, "Backup", "Started", null));
        Assert.Equal("Backup", (await d.Runs(id)).Single().CurrentStep);
        await d.Reports.StepAsync(Guid.NewGuid(), new RunStep(run.Id, "Hijack", "Started", null));
        Assert.Equal("Backup", (await d.Runs(id)).Single().CurrentStep);
    }

    // ---------------- manual rollback ----------------

    private async Task<(Guid reqId, Guid runId)> Deployed(Actor dep, Actor appr, Package pkg, string env)
    {
        var id = await d.SubmitApprovedAndRun(dep, appr, pkg, env);
        return (id, (await d.Runs(id)).Single().Id);
    }

    [Fact]
    public async Task Manual_rollback_outside_production_needs_no_approval_and_restores_the_recorded_backup()
    {
        var (dep, appr, pkg) = await Ready();
        var (_, runId) = await Deployed(dep, appr, pkg, "Dev");
        var rb = await d.Svc.RequestRollbackAsync(dep, runId, "bad release");
        Assert.True(rb.Ok, rb.Error);
        Assert.Equal(RequestStatus.Approved, (await d.Req(rb.Value)).Status);
        await d.Orch.TickAsync();
        var (_, cmd) = Assert.Single(d.E.Hub.Client.Rollbacks);
        Assert.StartsWith("bk-", cmd.BackupId);
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(rb.Value)).Status);
        await using var db = d.E.Db.CreateDbContext();
        Assert.Equal(RunStatus.RolledBack, (await db.DeploymentRuns.FirstAsync(r => r.Id == runId)).Status);
        Assert.Contains("Deployment.Rollback.Request:Success", await d.AuditActions());
        // it cannot be rolled back twice
        Assert.False((await d.Svc.RequestRollbackAsync(dep, runId, "again")).Ok);
    }

    [Fact]
    public async Task Manual_rollback_in_production_needs_an_approver()
    {
        await d.SetUp("Prod");
        var dep = d.Deployer("Prod"); var appr = d.Approver("Prod");
        var pkg = await d.Upload(dep, "Prod", Site());
        var rid = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Prod", directReason: "hotfix"));
        await d.Svc.ApproveAsync(appr, rid.Value); await d.Orch.TickAsync();
        var runId = (await d.Runs(rid.Value)).Single().Id;

        var rb = await d.Svc.RequestRollbackAsync(dep, runId, "regression");
        Assert.Equal(RequestStatus.Submitted, (await d.Req(rb.Value)).Status);
        await d.Orch.TickAsync();
        Assert.Empty(d.E.Hub.Client.Rollbacks);
        Assert.True((await d.Svc.ApproveAsync(appr, rb.Value)).Ok);
        await d.Orch.TickAsync();
        Assert.Single(d.E.Hub.Client.Rollbacks);
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(rb.Value)).Status);
    }

    [Fact]
    public async Task Only_the_latest_successful_deployment_of_a_target_can_be_rolled_back_and_it_needs_a_reason_and_the_role()
    {
        var (dep, appr, pkg) = await Ready();
        var (_, run1) = await Deployed(dep, appr, pkg, "Dev");
        var pkg2 = await d.Upload(dep, "Dev", Site("2"), "b2.zip");
        d.E.Clock.Advance(TimeSpan.FromMinutes(5));
        var (_, run2) = await Deployed(dep, appr, pkg2, "Dev");
        var old = await d.Svc.RequestRollbackAsync(dep, run1, "x");
        Assert.False(old.Ok); Assert.Contains("newer", old.Error);
        Assert.False((await d.Svc.RequestRollbackAsync(dep, run2, " ")).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.RequestRollbackAsync(d.Person("v", (Role.ProjectViewer, "Dev")), run2, "x"));
        Assert.True((await d.Svc.RequestRollbackAsync(dep, run2, "regression")).Ok);
    }

    [Fact]
    public async Task A_failed_run_cannot_be_rolled_back_by_hand()
    {
        var (dep, appr, pkg) = await Ready();
        d.DeployOutcome = c => new(c.RunId, RunResults.Failed, null, null, "Copy", "x", 0, 0, 0, 0, 1, null);
        var (_, run) = await Deployed(dep, appr, pkg, "Dev");
        Assert.False((await d.Svc.RequestRollbackAsync(dep, run, "x")).Ok);
    }

    // ---------------- package store retention ----------------

    [Fact]
    public async Task Packages_past_retention_are_deleted_unless_an_open_request_needs_them()
    {
        var (dep, appr, pkg) = await Ready();
        var used = await d.Upload(dep, "Dev", Site("2"), "b2.zip");
        await d.Svc.SubmitAsync(dep, d.Cmd(used, "Dev"));
        d.E.Clock.Advance(TimeSpan.FromDays(91));
        await d.Svc.ExpireDueAsync();             // the open request expired after 7 days, so nothing protects `used` any more
        var purged = await d.Maintenance.PurgePackagesAsync();
        Assert.Equal(2, purged);
        Assert.False(d.Store.Exists(pkg));
        Assert.Contains("Package.Purge:Success", await d.AuditActions());
    }

    [Fact]
    public async Task A_package_needed_by_an_open_request_is_kept_past_retention()
    {
        var (dep, appr, pkg) = await Ready();
        await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"));
        await using (var db = d.E.Db.CreateDbContext()) { (await db.Packages.FirstAsync()).ExpiresAt = d.E.Clock.GetUtcNow().AddDays(-1); await db.SaveChangesAsync(); }
        Assert.Equal(0, await d.Maintenance.PurgePackagesAsync());
        Assert.True(d.Store.Exists(pkg));
    }
}
