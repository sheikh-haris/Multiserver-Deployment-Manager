using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// Moves approved requests through execution: one target at a time, in order, stopping at the first failure.
/// It runs on a timer (not an event) so a restart of the portal loses nothing: all state is in the database.
/// </summary>
public sealed class DeploymentOrchestrator(
    IDbContextFactory<AppDbContext> dbf,
    AgentCommandService commands,
    AuditService audit,
    NotificationService notify,
    DeploymentEventBus bus,
    IOptions<DeploymentOptions> options,
    TimeProvider clock,
    ILogger<DeploymentOrchestrator> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private DeploymentOptions O => options.Value;

    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow();
            var ids = await db.DeploymentRequests.AsNoTracking()
                .Where(r => (r.Status == RequestStatus.Approved || (r.Status == RequestStatus.Scheduled && r.ScheduledAt <= now) || r.Status == RequestStatus.Running) && r.ExpiresAt >= now)
                .OrderBy(r => r.RequestedAt).Select(r => r.Id).ToListAsync(ct);
            var n = 0;
            foreach (var id in ids)
            {
                try { if (await AdvanceAsync(id, ct)) n++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Advancing request {Id} failed", id); }
            }
            return n;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Does the next thing the request needs. Returns true if something changed.</summary>
    public async Task<bool> AdvanceAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var req = await db.DeploymentRequests.FirstAsync(r => r.Id == requestId, ct);
        var now = clock.GetUtcNow();
        var comp = await db.Components.AsNoTracking().Include(c => c.ProtectedPaths).FirstAsync(c => c.Id == req.ComponentId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId, ct);
        var rts = await db.RequestTargets.Where(t => t.RequestId == requestId).OrderBy(t => t.DeployOrder).ToListAsync(ct);
        var runs = await db.DeploymentRuns.Where(r => r.RequestId == requestId).OrderBy(r => r.Sequence).ToListAsync(ct);

        // ---- a run is in flight: wait, or give up after the timeout ----
        var running = runs.FirstOrDefault(r => r.Status == RunStatus.Running);
        if (running is not null)
        {
            if (running.StartedAt is { } st && now - st > TimeSpan.FromMinutes(O.RunTimeoutMinutes))
            {
                running.Status = RunStatus.Failed; running.EndedAt = now; running.FailureStep = "Timeout";
                running.Message = $"No result from the agent within {O.RunTimeoutMinutes} minutes. Check the server: the agent may have stopped or the site may be offline (app_offline.htm).";
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(new AuditRecord("Deployment.Run.Timeout", "DeploymentRun", running.Id.ToString(), EnvironmentName: req.EnvironmentName, Result: "Failed", Detail: running.Message));
                await FinishAsync(db, req, project, comp, RequestStatus.Failed, running.Message, ct);
                return true;
            }
            return false;
        }

        if (req.Status == RequestStatus.Running && runs.Any(r => r.Status is RunStatus.Failed or RunStatus.RolledBack or RunStatus.NeedsReapproval))
        {
            // The result handler normally finishes the request; this is the safety net.
            var bad = runs.Last(r => r.Status is RunStatus.Failed or RunStatus.RolledBack or RunStatus.NeedsReapproval);
            await FinishAsync(db, req, project, comp, bad.Status == RunStatus.RolledBack ? RequestStatus.RolledBack : bad.Status == RunStatus.NeedsReapproval ? RequestStatus.NeedsReapproval : RequestStatus.Failed, bad.Message, ct);
            return true;
        }

        // ---- the next target ----
        var done = runs.Where(r => r.Status == RunStatus.Succeeded).Select(r => r.DeploymentTargetId).ToHashSet();
        var next = rts.FirstOrDefault(t => !done.Contains(t.DeploymentTargetId));
        if (next is null)
        {
            await FinishAsync(db, req, project, comp, RequestStatus.Succeeded, null, ct);
            return true;
        }

        var target = await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == next.DeploymentTargetId, ct);
        var server = await db.Servers.AsNoTracking().FirstAsync(s => s.Id == target.ServerId, ct);

        if (req.Status != RequestStatus.Running)
        {
            // ---- before the first target: check the request is still allowed ----
            var why = await CheckStillAllowedAsync(db, req, rts, project, ct);
            if (why is not null)
            {
                await ToReapprovalAsync(db, req, project, comp, why, ct);
                return true;
            }
            if (!commands.IsConnected(server.Id))
            {
                var detail = $"Waiting: the agent on {server.Name} is offline.";
                if (req.StatusDetail != detail) { req.StatusDetail = detail; await db.SaveChangesAsync(ct); bus.Publish(new DeploymentChanged(req.Id, null, false)); }
                return false;
            }
        }
        else if (!commands.IsConnected(server.Id))
        {
            // Mid-request. Give the agent time to come back, then stop.
            var lastEnd = runs.Max(r => r.EndedAt) ?? req.StartedAt ?? now;
            if (now - lastEnd > TimeSpan.FromMinutes(O.AgentOfflineGraceMinutes))
            {
                await FinishAsync(db, req, project, comp, RequestStatus.Failed, $"The agent on {server.Name} stayed offline for {O.AgentOfflineGraceMinutes} minutes. The remaining servers were not deployed.", ct);
                return true;
            }
            return false;
        }

        // ---- start the run ----
        if (req.Status != RequestStatus.Running)
        {
            RequestStateMachine.EnsureCanTransition(req.Status, RequestStatus.Running);
            req.Status = RequestStatus.Running; req.StartedAt = now; req.StatusDetail = null;
        }
        var run = new DeploymentRun { Id = Guid.NewGuid(), RequestId = req.Id, DeploymentTargetId = target.Id, Sequence = runs.Count + 1, Status = RunStatus.Running, StartedAt = now, CurrentStep = "Sending" };
        db.DeploymentRuns.Add(run);
        await db.SaveChangesAsync(ct);

        OpResult sent;
        var env = commands.NewEnvelope(server, TimeSpan.FromMinutes(10));
        if (req.Type == RequestType.Deploy)
        {
            var pkg = await db.Packages.AsNoTracking().FirstAsync(p => p.Id == req.PackageId, ct);
            var prot = DiffService.ProtectedFor(comp.ProtectedPaths, await NestedFolderQuery.ForAsync(db, target));
            var forced = await db.ForcedFiles.AsNoTracking().Where(f => f.RequestId == req.Id).Select(f => f.Path).ToListAsync(ct);
            var cmd = new DeployCommand(run.Id, req.Id, target.Id, target.PhysicalPath, target.IisSiteId, target.AppPath, pkg.Id, pkg.Sha256, pkg.Size, (DeployMode)req.Mode,
                prot.ConfigPatterns, prot.DataFolders, forced, O.BackupsToKeep, comp.HealthCheckPath, 0, O.HealthTimeoutSeconds,
                (comp.AllowedFileTypes ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries), pkg.AvResult == AvResult.ScanOnTarget);
            sent = await commands.SendDeployAsync(server, env, cmd);
        }
        else
        {
            var orig = await db.DeploymentRuns.AsNoTracking().FirstAsync(r => r.Id == req.RollbackOfRunId, ct);
            sent = await commands.SendRollbackAsync(server, env, new RollbackCommand(run.Id, req.Id, target.Id, target.PhysicalPath, target.IisSiteId, target.AppPath,
                orig.BackupId!, comp.HealthCheckPath, 0, O.HealthTimeoutSeconds));
        }
        await audit.WriteSystemAsync(new AuditRecord(req.Type == RequestType.Deploy ? "Deployment.Run.Start" : "Deployment.Rollback.Start", "DeploymentRun", run.Id.ToString(), server.Id, req.EnvironmentName,
            After: $"{project.Name}/{comp.Name} on {server.Name}; request {req.Id}", Result: sent.Ok ? "Success" : "Failed", Detail: sent.Error));
        if (!sent.Ok)
        {
            run.Status = RunStatus.Failed; run.EndedAt = now; run.FailureStep = "Send"; run.Message = sent.Error;
            await db.SaveChangesAsync(ct);
            await FinishAsync(db, req, project, comp, RequestStatus.Failed, sent.Error, ct);
        }
        else bus.Publish(new DeploymentChanged(req.Id, run.Id, false));
        return true;
    }

    // ---------- permission re-check ----------

    /// <summary>
    /// The roles that allowed this request must still be active when it runs. If someone's role was removed since they submitted or approved,
    /// the request goes back to Submitted for a fresh approval. (Entra group changes are only seen when the person signs in: documented limit.)
    /// </summary>
    private async Task<string?> CheckStillAllowedAsync(AppDbContext db, DeploymentRequest req, List<RequestTarget> rts, Project project, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var submit = (req.SubmitAssignmentIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        if (submit.Count > 0)
        {
            var assignments = await db.RoleAssignments.AsNoTracking().Where(a => submit.Contains(a.Id)).ToListAsync(ct);
            if (!assignments.Any(a => AccessEvaluator.IsActive(a, now))) return "The role that allowed the requester to submit this has been removed.";
        }
        if (req.Type == RequestType.Deploy || req.EnvironmentName == EnvironmentNames.Prod)
        {
            var approvals = await db.Approvals.AsNoTracking().Where(a => a.RequestId == req.Id && a.Decision == ApprovalDecision.Approved).ToListAsync(ct);
            var ids = approvals.Where(a => a.AuthorizedByAssignmentId != null).Select(a => a.AuthorizedByAssignmentId!.Value).ToList();
            var live = await db.RoleAssignments.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
            var valid = approvals.Count(a => a.AuthorizedByAssignmentId is { } aid && live.Any(l => l.Id == aid && AccessEvaluator.IsActive(l, now)));
            if (valid < req.ApprovalsRequired) return "An approver's role was removed after approving. The request needs a fresh approval.";
        }
        foreach (var rt in rts)
        {
            var t = await db.DeploymentTargets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rt.DeploymentTargetId, ct);
            if (t is null || t.Status != TargetStatus.Active) return "A target is no longer in a usable state (" + (t?.Status.ToString() ?? "removed") + ").";
            if (req.Type == RequestType.Deploy && !string.Equals(t.PhysicalPath, rt.PhysicalPathAtSubmit, StringComparison.OrdinalIgnoreCase))
                return $"The IIS folder of a target changed after the request was submitted ({rt.PhysicalPathAtSubmit} → {t.PhysicalPath}).";
        }
        var pkg = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.PackageId, ct);
        if (req.Type == RequestType.Deploy && (pkg is null || pkg.DeletedAt != null)) return "The package is no longer in the store.";
        return null;
    }

    private async Task ToReapprovalAsync(AppDbContext db, DeploymentRequest req, Project project, Component comp, string why, CancellationToken ct)
    {
        var was = req.Status;
        RequestStateMachine.EnsureCanTransition(req.Status, RequestStatus.NeedsReapproval);
        req.Status = RequestStatus.NeedsReapproval; req.StatusDetail = why;
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(new AuditRecord("Deployment.NeedsReapproval", "DeploymentRequest", req.Id.ToString(), EnvironmentName: req.EnvironmentName, Before: was.ToString(), After: why));
        await FinishNotifyAsync(req, project, comp, "needs approval again: " + why);
    }

    // ---------- finishing ----------

    public async Task FinishAsync(AppDbContext db, DeploymentRequest req, Project project, Component comp, RequestStatus status, string? message, CancellationToken ct)
    {
        if (RequestStateMachine.IsTerminal(req.Status) && req.Status != status) return;
        if (req.Status != status) RequestStateMachine.EnsureCanTransition(req.Status, status);
        req.Status = status; req.EndedAt = status == RequestStatus.NeedsReapproval ? null : clock.GetUtcNow();
        if (message is not null) req.StatusDetail = message;
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(new AuditRecord("Deployment.Finish", "DeploymentRequest", req.Id.ToString(), EnvironmentName: req.EnvironmentName,
            After: $"{project.Name}/{comp.Name}: {status}", Result: status == RequestStatus.Succeeded ? "Success" : "Failed", Detail: message));
        await FinishNotifyAsync(req, project, comp, status.ToString().ToLowerInvariant() + (message is null ? "" : ": " + message));
    }

    private async Task FinishNotifyAsync(DeploymentRequest r, Project p, Component c, string what)
    {
        await notify.EnqueueAsync(r.Id, "Finished", [p.NotificationEmail, r.RequestedByEmail],
            $"[IIS Control Center] {p.Name}/{c.Name} → {r.EnvironmentName}: {what.Split(':')[0]}",
            $"The {(r.Type == RequestType.Rollback ? "rollback" : "deployment")} of {c.Name} ({p.Name}) to {r.EnvironmentName}, requested by {r.RequestedBy}, is {what}." +
            (r.SelfApproved ? "\nThis request was approved by its own requester." : "") + (notify.Link(r.Id) is { } l ? $"\nOpen: {l}" : ""));
        bus.Publish(new DeploymentChanged(r.Id, null, false));
    }
}
