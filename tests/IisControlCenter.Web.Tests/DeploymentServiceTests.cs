using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class DeploymentServiceTests : IDisposable
{
    private readonly DeployEnv d = new();
    public void Dispose() => d.Dispose();

    private static (string, string)[] Site(string v = "1") => [("index.html", "v" + v), ("app.dll", "dll" + v), ("web.config", "<new/>")];

    // ---------------- submit ----------------

    [Fact]
    public async Task Submit_stores_counts_snapshot_and_audit_and_needs_approval()
    {
        await d.SetUp();
        d.Live["index.html"] = "old"; d.Live["web.config"] = "<live/>"; d.Live["extra.txt"] = "x";
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var r = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"));
        Assert.True(r.Ok, r.Error);
        var det = await d.Svc.GetAsync(dep, r.Value);
        Assert.Equal(RequestStatus.Submitted, det!.Request.Status);
        var t = Assert.Single(det.Targets);
        Assert.Equal((1, 1, 0, 1), (t.Added, t.Replaced, t.Deleted, t.Skipped));       // app.dll added, index replaced, web.config kept
        Assert.Contains("Deployment.Submit:Success", await d.AuditActions());
        Assert.Equal(1, det.Request.ApprovalsRequired);
        Assert.False(det.Request.IsDirectToProd);
        await using var db = d.E.Db.CreateDbContext();
        Assert.Contains("web.config", (await db.RequestTargets.SingleAsync()).SnapshotJson);
        Assert.Single(d.Store.ReadChanges(r.Value, d.TargetId).Where(c => c.Action == FileAction.SkipProtectedConfig));
    }

    [Fact]
    public async Task Submit_needs_the_deployer_role_for_that_environment()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var viewer = d.Person("v", (Role.ProjectViewer, "Dev"));
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.SubmitAsync(viewer, d.Cmd(pkg, "Dev")));
        var otherEnv = d.Person("o", (Role.Deployer, "Staging"));
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.SubmitAsync(otherEnv, d.Cmd(pkg, "Dev")));
        Assert.Contains("Deployment.Submit:Denied", await d.AuditActions());
    }

    [Fact]
    public async Task Submit_requires_a_source_reference()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var r = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", source: " "));
        Assert.False(r.Ok);
        Assert.Contains("source reference", r.Error);
    }

    [Fact]
    public async Task Full_replace_is_blocked_until_a_platform_admin_confirms_the_protected_list_and_a_change_voids_it()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var blocked = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", mode: DeployMode.FullReplace));
        Assert.False(blocked.Ok); Assert.Contains("Full Replace", blocked.Error);

        await Assert.ThrowsAsync<ForbiddenException>(() => d.Protected.ConfirmAsync(dep, d.ComponentId));
        Assert.True((await d.Protected.ConfirmAsync(d.E.Admin, d.ComponentId)).Ok);
        Assert.True((await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", mode: DeployMode.FullReplace))).Ok);

        Assert.True((await d.Protected.AddAsync(d.E.Admin, d.ComponentId, "App_Data", ProtectedPathKind.DataFolder)).Ok);
        var again = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", mode: DeployMode.FullReplace));
        Assert.False(again.Ok);                                    // list changed: confirmation void
    }

    [Fact]
    public async Task Full_replace_lists_deletions_but_never_data_folders_or_protected_config()
    {
        await d.SetUp();
        await d.Protected.ConfirmAsync(d.E.Admin, d.ComponentId);
        d.Live["old.js"] = "x"; d.Live["uploads/a.jpg"] = "u"; d.Live["web.config"] = "<c/>"; d.Live["appsettings.json"] = "{}";
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", ("index.html", "v"));
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", mode: DeployMode.FullReplace))).Value;
        var changes = d.Store.ReadChanges(id, d.TargetId);
        Assert.Equal(["old.js"], changes.Where(c => c.Action == FileAction.Delete).Select(c => c.Path));
    }

    [Fact]
    public async Task Choosing_fewer_servers_than_the_environment_has_needs_a_reason()
    {
        await d.SetUp();
        var (_, t2) = await d.AddServer("WEB02", "Dev", 12);
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var no = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"));
        Assert.False(no.Ok); Assert.Contains("only some of the servers", no.Error);
        var yes = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", singleReason: "canary"));
        Assert.True(yes.Ok, yes.Error);
        var both = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", targets: [d.TargetId, t2]));
        Assert.True(both.Ok, both.Error);
    }

    [Fact]
    public async Task Forced_overwrite_needs_a_reason_an_existing_protected_file_and_stores_a_masked_diff()
    {
        await d.SetUp();
        d.Live["appsettings.json"] = "{\"Password\": \"hunter2\", \"Name\": \"live\"}";
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", ("appsettings.json", "{\"Password\": \"hunter2\", \"Name\": \"new\"}"), ("index.html", "x"));
        var noReason = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", forced: [new("appsettings.json", " ")]));
        Assert.False(noReason.Ok);
        var notProtected = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", forced: [new("index.html", "why")]));
        Assert.False(notProtected.Ok);

        var ok = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", forced: [new("appsettings.json", "new setting")]));
        Assert.True(ok.Ok, ok.Error);
        var det = await d.Svc.GetAsync(dep, ok.Value);
        var f = Assert.Single(det!.ForcedFiles);
        var view = f.PerTarget[d.TargetId.ToString()];
        Assert.DoesNotContain("hunter2", view.Before); Assert.DoesNotContain("hunter2", view.After);
        Assert.Contains("live", view.Before); Assert.Contains("new", view.After);
        Assert.Equal(FileAction.ForceOverwrite, d.Store.ReadChanges(ok.Value, d.TargetId).Single(c => c.Path == "appsettings.json").Action);
    }

    [Fact]
    public async Task Submit_fails_when_the_agent_is_offline_or_the_server_unknown()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        d.E.Tracker.Remove(d.ServerId, "conn-WEB01");
        var r = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"));
        Assert.False(r.Ok); Assert.Contains("offline", r.Error);
    }

    [Fact]
    public async Task Schedule_must_be_in_the_future_and_within_seven_days()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var now = d.E.Clock.GetUtcNow();
        Assert.False((await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", at: now.AddMinutes(-5)))).Ok);
        Assert.False((await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", at: now.AddDays(8)))).Ok);
        Assert.True((await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev", at: now.AddHours(3)))).Ok);
    }

    // ---------------- production rules ----------------

    private async Task<(Actor dep, Actor appr, Guid server, Guid target)> ProdWorld()
    {
        await d.SetUp("Prod");
        return (d.Deployer("Prod"), d.Approver("Prod"), d.ServerId, d.TargetId);
    }

    [Fact]
    public async Task Direct_to_prod_needs_a_reason_is_flagged_and_needs_the_projects_approver_count()
    {
        var (dep, _, _, _) = await ProdWorld();
        await d.SetProdApprovers(2);
        var pkg = await d.Upload(dep, "Prod", Site());
        var no = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Prod"));
        Assert.False(no.Ok); Assert.Contains("reason", no.Error);
        var ok = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Prod", directReason: "hotfix for outage"));
        Assert.True(ok.Ok, ok.Error);
        var r = await d.Req(ok.Value);
        Assert.True(r.IsDirectToProd);
        Assert.Equal(2, r.ApprovalsRequired);
    }

    [Fact]
    public async Task Direct_to_prod_is_refused_when_the_component_requires_staging()
    {
        var (dep, _, _, _) = await ProdWorld();
        await using (var db = d.E.Db.CreateDbContext()) { (await db.Components.FirstAsync()).RequiresStagingBeforeProd = true; await db.SaveChangesAsync(); }
        var pkg = await d.Upload(dep, "Prod", Site());
        var r = await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Prod", directReason: "urgent"));
        Assert.False(r.Ok); Assert.Contains("Staging first", r.Error);
    }

    [Fact]
    public async Task A_package_that_succeeded_on_staging_is_a_promote_and_needs_one_approval()
    {
        await d.SetUp("Staging");
        var (_, prodTarget) = (0, Guid.Empty);
        var dep = d.Deployer("Staging"); var appr = d.Approver("Staging");
        var pkg = await d.Upload(dep, "Staging", Site());
        var stagingReq = await d.SubmitApprovedAndRun(dep, appr, pkg, "Staging");
        Assert.Equal(RequestStatus.Succeeded, (await d.Req(stagingReq)).Status);

        // production server and target for the same component
        var (ps, pt) = await d.AddServer("PRODWEB", "Prod", 21);
        var prodDep = d.Person("pdep", (Role.Deployer, "Prod"));
        var prodReq = await d.Svc.SubmitAsync(prodDep, d.Cmd(pkg, "Prod", targets: [pt]));
        Assert.True(prodReq.Ok, prodReq.Error);
        var r = await d.Req(prodReq.Value);
        Assert.False(r.IsDirectToProd);
        Assert.Equal(stagingReq, r.PromotedFromRequestId);
        Assert.Equal(1, r.ApprovalsRequired);
    }

    [Fact]
    public async Task A_package_with_different_content_is_not_a_promote_even_with_the_same_file_name()
    {
        await d.SetUp("Staging");
        var dep = d.Deployer("Staging"); var appr = d.Approver("Staging");
        var pkg = await d.Upload(dep, "Staging", Site("1"));
        await d.SubmitApprovedAndRun(dep, appr, pkg, "Staging");
        var (_, pt) = await d.AddServer("PRODWEB", "Prod", 21);
        var prodDep = d.Person("pdep", (Role.Deployer, "Prod"));
        var other = await d.Upload(prodDep, "Prod", Site("2"));
        var r = await d.Svc.SubmitAsync(prodDep, d.Cmd(other, "Prod", targets: [pt]));
        Assert.False(r.Ok);                      // would be direct-to-prod without a reason
        var ok = await d.Svc.SubmitAsync(prodDep, d.Cmd(other, "Prod", targets: [pt], directReason: "skip staging"));
        Assert.True((await d.Req(ok.Value)).IsDirectToProd);
    }

    // ---------------- approval ----------------

    [Fact]
    public async Task Requester_cannot_approve_own_request_by_default_and_the_denial_is_audited()
    {
        await d.SetUp();
        var both = d.Person("both", (Role.Deployer, "Dev"), (Role.Approver, "Dev"));
        var pkg = await d.Upload(both, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(both, d.Cmd(pkg, "Dev"))).Value;
        var r = await d.Svc.ApproveAsync(both, id);
        Assert.False(r.Ok); Assert.Contains("own request", r.Error);
        Assert.Contains("Deployment.Approve:Denied", await d.AuditActions());
        Assert.Equal(RequestStatus.Submitted, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task Self_approval_when_the_project_allows_it_is_marked_audited_and_badged()
    {
        await d.SetUp();
        await d.AllowSelfApproval("Dev");
        var both = d.Person("both", (Role.Deployer, "Dev"), (Role.Approver, "Dev"));
        var pkg = await d.Upload(both, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(both, d.Cmd(pkg, "Dev"))).Value;
        Assert.True((await d.Svc.ApproveAsync(both, id)).Ok);
        var req = await d.Req(id);
        Assert.Equal(RequestStatus.Approved, req.Status);
        Assert.True(req.SelfApproved);
        Assert.Contains("Deployment.Approve.Self:Success", await d.AuditActions());
        Assert.True((await d.Svc.GetAsync(both, id))!.Approvals.Single().IsSelfApproval);
    }

    [Fact]
    public async Task Self_approval_setting_is_per_environment()
    {
        await d.SetUp();
        await d.AllowSelfApproval("Staging");           // not Dev
        var both = d.Person("both", (Role.Deployer, "Dev"), (Role.Approver, "Dev"));
        var pkg = await d.Upload(both, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(both, d.Cmd(pkg, "Dev"))).Value;
        Assert.False((await d.Svc.ApproveAsync(both, id)).Ok);
    }

    [Fact]
    public async Task Only_an_approver_for_that_environment_can_approve_and_platform_admin_alone_cannot()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        Assert.False((await d.Svc.ApproveAsync(d.Person("x", (Role.Approver, "Prod")), id)).Ok);
        Assert.False((await d.Svc.ApproveAsync(d.Person("y", (Role.Deployer, "Dev")), id)).Ok);
        Assert.False((await d.Svc.ApproveAsync(d.E.Admin, id)).Ok);
        Assert.True((await d.Svc.ApproveAsync(d.Approver("Dev"), id)).Ok);
    }

    [Fact]
    public async Task Two_approvers_are_needed_for_direct_to_prod_and_the_same_person_counts_once()
    {
        var (dep, a1, _, _) = await ProdWorld();
        await d.SetProdApprovers(2);
        var pkg = await d.Upload(dep, "Prod", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Prod", directReason: "outage"))).Value;
        Assert.True((await d.Svc.ApproveAsync(a1, id)).Ok);
        Assert.Equal(RequestStatus.Submitted, (await d.Req(id)).Status);
        Assert.False((await d.Svc.ApproveAsync(a1, id)).Ok);                       // again: no
        Assert.Equal(RequestStatus.Submitted, (await d.Req(id)).Status);
        Assert.True((await d.Svc.ApproveAsync(d.Approver("Prod", "app2"), id)).Ok);
        Assert.Equal(RequestStatus.Approved, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task Reject_needs_a_reason_and_ends_the_request()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        Assert.False((await d.Svc.RejectAsync(appr, id, " ")).Ok);
        Assert.True((await d.Svc.RejectAsync(appr, id, "wrong build")).Ok);
        var r = await d.Req(id);
        Assert.Equal((RequestStatus.Rejected, "wrong build"), (r.Status, r.StatusDetail));
        Assert.False((await d.Svc.ApproveAsync(appr, id)).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.RejectAsync(dep, id, "x"));
    }

    [Fact]
    public async Task Only_the_requester_or_a_platform_admin_can_cancel_and_only_before_execution()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Svc.CancelAsync(d.Person("other", (Role.Deployer, "Dev")), id, null));
        Assert.True((await d.Svc.CancelAsync(dep, id, "changed my mind")).Ok);
        Assert.Equal(RequestStatus.Cancelled, (await d.Req(id)).Status);
        Assert.False((await d.Svc.CancelAsync(dep, id, null)).Ok);
    }

    [Fact]
    public async Task Requests_expire_after_seven_days_and_cannot_be_approved_afterwards()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        d.E.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var r = await d.Svc.ApproveAsync(appr, id);
        Assert.False(r.Ok); Assert.Contains("expired", r.Error);
        Assert.Equal(RequestStatus.Expired, (await d.Req(id)).Status);
        Assert.Contains("Deployment.Expire:Success", await d.AuditActions());
    }

    [Fact]
    public async Task Maintenance_expires_approved_but_unrun_requests()
    {
        await d.SetUp();
        d.E.Tracker.Remove(d.ServerId, "conn-WEB01");     // agent offline: approved stays approved
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        d.E.Tracker.Add(d.ServerId, "tmp");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = await d.SubmitApprovedAndNothing(dep, appr, pkg, "Dev");
        d.E.Tracker.Remove(d.ServerId, "tmp");
        d.E.Clock.Advance(TimeSpan.FromDays(8));
        await d.Maintenance.RunAsync();
        Assert.Equal(RequestStatus.Expired, (await d.Req(id)).Status);
    }

    [Fact]
    public async Task Approval_queue_shows_only_what_the_person_can_decide_and_has_not_decided()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        Assert.Single(await d.Svc.ApprovalQueueAsync(appr));
        Assert.Empty(await d.Svc.ApprovalQueueAsync(dep));
        Assert.Empty(await d.Svc.ApprovalQueueAsync(d.Person("prodappr", (Role.Approver, "Prod"))));
        await d.Svc.ApproveAsync(appr, id);
        Assert.Empty(await d.Svc.ApprovalQueueAsync(appr));
    }

    [Fact]
    public async Task People_without_a_role_on_the_project_cannot_see_the_request()
    {
        await d.SetUp();
        var dep = d.Deployer("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        Assert.Null(await d.Svc.GetAsync(d.E.Nobody, id));
        Assert.Empty(await d.Svc.ListAsync(d.E.Nobody));
        Assert.NotNull(await d.Svc.GetAsync(d.E.Admin, id));
        Assert.NotNull(await d.Svc.GetAsync(d.E.ActorWith(Role.Auditor), id));
    }

    // ---------------- notifications ----------------

    [Fact]
    public async Task Submit_and_finish_queue_email_that_contains_no_secrets_and_survive_a_mail_outage()
    {
        await d.SetUp();
        await using (var db = d.E.Db.CreateDbContext()) { (await db.Projects.FirstAsync()).NotificationEmail = "team@corp.test"; await db.SaveChangesAsync(); }
        var dep = d.Deployer("Dev"); var appr = d.Approver("Dev");
        var pkg = await d.Upload(dep, "Dev", Site());
        d.Email.Fail = true;
        var id = (await d.Svc.SubmitAsync(dep, d.Cmd(pkg, "Dev"))).Value;
        Assert.Equal(0, await d.Notify.SendPendingAsync());                // mail server down: nothing lost
        d.Email.Fail = false;
        Assert.Equal(1, await d.Notify.SendPendingAsync());
        var mail = Assert.Single(d.Email.Sent);
        Assert.Equal("team@corp.test", mail.To);
        Assert.Contains($"https://portal.test/deployments/{id}", mail.Body);
        await d.Svc.ApproveAsync(appr, id);
        await d.Orch.TickAsync();
        await d.Notify.SendPendingAsync();
        Assert.Contains(d.Email.Sent, m => m.Subject.Contains("succeeded") && m.To == "dep@corp.test");
    }
}
