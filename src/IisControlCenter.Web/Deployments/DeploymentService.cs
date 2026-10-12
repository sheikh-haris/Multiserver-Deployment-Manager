using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Deployments;

public sealed record SubmitCommand(
    Guid ComponentId, string Environment, Guid PackageId, IReadOnlyList<Guid> TargetIds, DeployMode Mode,
    string? SourceReference, string? Notes, string? DirectToProdReason, string? SingleServerReason,
    IReadOnlyList<ForcedFileInput> ForcedFiles, DateTimeOffset? ScheduledAt);

/// <summary>What the wizard shows before submit: the live comparison per target plus the facts the rules depend on.</summary>
public sealed record PreviewResult(
    IReadOnlyList<TargetDiff> Targets, bool IsProduction, bool IsDirectToProd, bool IsPromote, Guid? PromotedFromRequestId,
    bool ProtectedListConfirmed, bool HasDataFolders, int ActiveTargets, int ApprovalsRequired, bool SelfApprovalAllowed);

public sealed record RequestRow(
    Guid Id, RequestType Type, RequestStatus Status, string ProjectName, Guid ProjectId, string ComponentName, string EnvironmentName,
    string PackageName, string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ScheduledAt,
    bool IsDirectToProd, bool SelfApproved, int ApprovalsRequired, int ApprovalsGiven, int TargetCount, string? SourceReference);

public sealed record TargetDetail(
    Guid TargetId, string ServerName, string SiteName, string AppPath, string PhysicalPath, int Order,
    int Added, int Replaced, int Deleted, int Skipped, int Unchanged, string? DeletionWarning);

public sealed record RunDetail(
    Guid RunId, Guid TargetId, string ServerName, int Sequence, RunStatus Status, string? CurrentStep, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
    string? FailureStep, string? Message, string? HealthDetail, string? BackupId, long DurationMs, int Added, int Replaced, int Deleted, int Skipped,
    bool CanRollback);

public sealed record ForcedFileDetail(string Path, string Reason, IReadOnlyDictionary<string, ForcedFileView> PerTarget);
public sealed record ForcedFileView(string? Before, string? After, string? Note, IReadOnlyList<DiffLine>? Lines);

public sealed record RequestDetail(
    DeploymentRequest Request, Project Project, Component Component, Package Package,
    IReadOnlyList<TargetDetail> Targets, IReadOnlyList<ForcedFileDetail> ForcedFiles, IReadOnlyList<Approval> Approvals,
    IReadOnlyList<RunDetail> Runs, string? PartialBanner, bool CanApprove, string? CannotApproveReason, bool CanCancel, bool CanReschedule,
    IReadOnlyList<string> PackageWarnings, bool SelfApprovalAllowed);

/// <summary>Submit, approve, reject, cancel, reschedule, roll back, and read requests. Every rule lives in Core (<see cref="SubmitRules"/>, <see cref="ApprovalRules"/>, <see cref="RequestStateMachine"/>).</summary>
public sealed class DeploymentService(
    IDbContextFactory<AppDbContext> dbf,
    DiffService diff,
    IPackageStore store,
    ProtectedListService protectedLists,
    AuditService audit,
    NotificationService notify,
    DeploymentEventBus bus,
    IOptions<DeploymentOptions> options,
    TimeProvider clock)
{
    private static readonly SemaphoreSlim ApprovalGate = new(1, 1);
    private DeploymentOptions O => options.Value;

    // =========================== preview / submit ===========================

    private async Task<(Component comp, Project project, Package pkg, List<DeploymentTarget> targets, Dictionary<Guid, Server> servers)?> LoadAsync(
        AppDbContext db, Actor actor, Guid componentId, string environment, Guid packageId, IReadOnlyList<Guid> targetIds, string auditAction)
    {
        var comp = await db.Components.AsNoTracking().Include(c => c.ProtectedPaths).FirstOrDefaultAsync(c => c.Id == componentId);
        if (comp is null) return null;
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
        if (!actor.Access.CanOnProject(ProjectPermission.SubmitDeployment, project.Id, environment))
        {
            await audit.WriteAsync(actor, new AuditRecord(auditAction, "Component", componentId.ToString(), EnvironmentName: environment, Result: "Denied"));
            throw new ForbiddenException("You do not hold the Deployer role for this project and environment.");
        }
        var pkg = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId && p.ComponentId == componentId && p.DeletedAt == null);
        if (pkg is null) return null;
        var targets = await db.DeploymentTargets.AsNoTracking()
            .Where(t => t.ComponentId == componentId && t.EnvironmentName == environment && targetIds.Contains(t.Id)).OrderBy(t => t.DeployOrder).ToListAsync();
        var sids = targets.Select(t => t.ServerId).Distinct().ToList();
        var servers = await db.Servers.AsNoTracking().Where(s => sids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        return (comp, project, pkg, targets, servers);
    }

    private async Task<(bool isPromote, Guid? from)> StagingProofAsync(AppDbContext db, Component comp, Package pkg)
    {
        var sha = pkg.Sha256;
        var from = await db.DeploymentRequests.AsNoTracking()
            .Where(r => r.ComponentId == comp.Id && r.Type == RequestType.Deploy && r.EnvironmentName == EnvironmentNames.Staging && r.Status == RequestStatus.Succeeded)
            .Join(db.Packages, r => r.PackageId, p => p.Id, (r, p) => new { r.Id, p.Sha256, r.EndedAt })
            .Where(x => x.Sha256 == sha).OrderByDescending(x => x.EndedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
        return (from is not null, from);
    }

    public async Task<OpResult<PreviewResult>> PreviewAsync(Actor actor, SubmitCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var l = await LoadAsync(db, actor, cmd.ComponentId, cmd.Environment, cmd.PackageId, cmd.TargetIds, "Deployment.Preview");
        if (l is null) return OpResult<PreviewResult>.Fail("Component or package not found.");
        var (comp, project, pkg, targets, servers) = l.Value;
        if (targets.Count == 0) return OpResult<PreviewResult>.Fail("Choose at least one target.");

        var prot = DiffService.ProtectedFor(comp.ProtectedPaths);
        var forced = cmd.ForcedFiles.Select(f => f.Path).ToHashSet(RelativePaths.Comparer);
        var diffs = new List<TargetDiff>();
        foreach (var t in targets) diffs.Add(await diff.ComputeAsync(pkg, servers[t.ServerId], t, cmd.Mode, prot, forced, ct));

        var isProd = cmd.Environment == EnvironmentNames.Prod;
        var (proof, from) = isProd ? await StagingProofAsync(db, comp, pkg) : (false, null);
        var direct = isProd && !proof;
        var conf = await protectedLists.GetConfirmationAsync(comp.Id);
        var selfAllowed = await SelfApprovalAllowedAsync(db, project.Id, cmd.Environment);
        var active = await db.DeploymentTargets.CountAsync(t => t.ComponentId == comp.Id && t.EnvironmentName == cmd.Environment && t.Status == TargetStatus.Active, ct);
        return OpResult<PreviewResult>.Success(new PreviewResult(diffs, isProd, direct, isProd && proof, from, conf is not null,
            prot.DataFolders.Count > 0, active, ApprovalRules.Required(direct, project.ProdApproversRequired), selfAllowed));
    }

    public async Task<OpResult<Guid>> SubmitAsync(Actor actor, SubmitCommand cmd, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var l = await LoadAsync(db, actor, cmd.ComponentId, cmd.Environment, cmd.PackageId, cmd.TargetIds, "Deployment.Submit");
        if (l is null) return OpResult<Guid>.Fail("Component or package not found.");
        var (comp, project, pkg, targets, servers) = l.Value;
        var now = clock.GetUtcNow();

        var errors = new List<string>();
        if (!project.IsActive) errors.Add("The project is inactive.");
        if (targets.Count != cmd.TargetIds.Distinct().Count()) errors.Add("One of the chosen targets does not belong to this component and environment.");
        foreach (var t in targets)
        {
            if (t.Status != TargetStatus.Active) errors.Add($"The target on {servers[t.ServerId].Name} is {t.Status}. Fix the link first.");
            var s = servers[t.ServerId];
            if (s.EnvironmentName is null || s.AgentStatus == AgentStatus.Revoked) errors.Add($"{s.Name} is not an approved server.");
        }
        if (pkg.ExpiresAt < now) errors.Add("The package has passed its retention period. Upload it again.");
        if (!store.Exists(pkg)) errors.Add("The package file is missing from the store.");
        if (cmd.ScheduledAt is { } sch)
        {
            if (sch <= now.AddMinutes(1)) errors.Add("The scheduled time must be in the future.");
            if (sch >= now.Add(ApprovalRules.RequestLifetime)) errors.Add("The scheduled time must be within 7 days (the request expires after 7 days).");
        }
        if (errors.Count > 0) return OpResult<Guid>.Fail(string.Join(" ", errors));

        var isProd = cmd.Environment == EnvironmentNames.Prod;
        var (proof, from) = isProd ? await StagingProofAsync(db, comp, pkg) : (false, null);
        var direct = isProd && !proof;
        var prot = DiffService.ProtectedFor(comp.ProtectedPaths);
        var forcedSet = cmd.ForcedFiles.Select(f => f.Path).ToHashSet(RelativePaths.Comparer);

        // Fresh look at every target: this is what the approver will see and what the agent compares against.
        var diffs = new List<TargetDiff>();
        foreach (var t in targets)
        {
            var d = await diff.ComputeAsync(pkg, servers[t.ServerId], t, cmd.Mode, prot, forcedSet, ct);
            if (!d.Ok) return OpResult<Guid>.Fail($"Could not read {d.ServerName}: {d.Error}");
            diffs.Add(d);
        }

        var conf = await protectedLists.GetConfirmationAsync(comp.Id);
        var active = await db.DeploymentTargets.CountAsync(t => t.ComponentId == comp.Id && t.EnvironmentName == cmd.Environment && t.Status == TargetStatus.Active, ct);
        var existingConfig = diffs.SelectMany(d => d.Changes).Where(c => c.Action is FileAction.SkipProtectedConfig or FileAction.ForceOverwrite).Select(c => c.Path).Distinct().ToList();
        var input = new SubmitInput(RequestType.Deploy, isProd, direct, isProd && proof, comp.RequiresStagingBeforeProd, cmd.SourceReference?.Trim(), cmd.Notes?.Trim(),
            cmd.DirectToProdReason?.Trim(), cmd.SingleServerReason?.Trim(), active, targets.Count,
            pkg.AvResult is AvResult.Clean or AvResult.ScanOnTarget, cmd.Mode, conf is not null, prot.DataFolders.Count > 0, cmd.ForcedFiles, existingConfig);
        var problems = SubmitRules.Validate(input);
        if (problems.Count > 0) return OpResult<Guid>.Fail(string.Join(" ", problems));

        // Masked before/after of every forced file, per target.
        var forcedRows = new List<ForcedFile>();
        foreach (var f in cmd.ForcedFiles)
        {
            var per = new Dictionary<string, object>();
            foreach (var t in targets)
            {
                var m = await diff.MaskedAsync(pkg, servers[t.ServerId], t, f.Path, prot, ct);
                per[t.Id.ToString()] = new { before = m.Before, after = m.After, note = m.Ok ? m.Note : "Could not read the live file: " + m.Error };
            }
            forcedRows.Add(new ForcedFile { Id = Guid.NewGuid(), Path = f.Path, Reason = f.Reason!.Trim(), MaskedDiffJson = JsonSerializer.Serialize(per) });
        }

        var req = new DeploymentRequest
        {
            Id = Guid.NewGuid(), Type = RequestType.Deploy, ComponentId = comp.Id, EnvironmentName = cmd.Environment, PackageId = pkg.Id, Mode = (int)cmd.Mode,
            SourceReference = input.SourceReference, Notes = string.IsNullOrWhiteSpace(cmd.Notes) ? null : cmd.Notes!.Trim(), IsDirectToProd = direct,
            DirectToProdReason = direct ? cmd.DirectToProdReason!.Trim() : null, SingleServerReason = targets.Count < active ? cmd.SingleServerReason?.Trim() : null,
            PromotedFromRequestId = from, Status = RequestStatus.Submitted, RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedByEmail = actor.Email,
            RequestedAt = now, ScheduledAt = cmd.ScheduledAt, ExpiresAt = now.Add(ApprovalRules.RequestLifetime),
            ApprovalsRequired = ApprovalRules.Required(direct, project.ProdApproversRequired),
            SubmitAssignmentIds = string.Join(',', actor.Access.AssignmentsFor(ProjectPermission.SubmitDeployment, project.Id, cmd.Environment).Select(a => a.Id)),
        };
        db.DeploymentRequests.Add(req);
        var order = 0;
        foreach (var (t, d) in targets.Zip(diffs))
        {
            db.RequestTargets.Add(new RequestTarget
            {
                Id = Guid.NewGuid(), RequestId = req.Id, DeploymentTargetId = t.Id, DeployOrder = order++, PhysicalPathAtSubmit = t.PhysicalPath,
                SnapshotJson = JsonSerializer.Serialize(d.Snapshot), DeletesJson = JsonSerializer.Serialize(d.Deletes),
                Added = d.Count(FileAction.Add), Replaced = d.Count(FileAction.Replace) + d.Count(FileAction.ForceOverwrite), Deleted = d.Count(FileAction.Delete),
                Skipped = d.Count(FileAction.SkipProtectedConfig) + d.Count(FileAction.SkipProtectedData), Unchanged = d.Count(FileAction.Unchanged), DeletionWarning = d.DeletionWarning,
            });
            store.WriteChanges(req.Id, t.Id, d.Changes);
        }
        foreach (var f in forcedRows) { f.RequestId = req.Id; db.ForcedFiles.Add(f); }
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(actor, new AuditRecord("Deployment.Submit", "DeploymentRequest", req.Id.ToString(), EnvironmentName: cmd.Environment,
            After: $"{project.Name}/{comp.Name}; package={pkg.FileName} sha256={pkg.Sha256[..12]}…; mode={cmd.Mode}; targets={targets.Count}; source={req.SourceReference}" +
                   (direct ? $"; DIRECT-TO-PROD reason={req.DirectToProdReason}" : isProd ? "; promote" : "") +
                   (forcedRows.Count > 0 ? $"; FORCED={string.Join(',', forcedRows.Select(f => f.Path))}" : "")));
        await NotifyNeedsApprovalAsync(req, project, comp, actor.UserName);
        bus.Publish(new DeploymentChanged(req.Id, null, false));
        return OpResult<Guid>.Success(req.Id);
    }

    private Task NotifyNeedsApprovalAsync(DeploymentRequest r, Project p, Component c, string by) =>
        notify.EnqueueAsync(r.Id, "NeedsApproval", [p.NotificationEmail],
            $"[IIS Control Center] Approval needed: {p.Name}/{c.Name} → {r.EnvironmentName}{(r.IsDirectToProd ? " (DIRECT TO PROD)" : "")}",
            $"{by} asked to deploy {c.Name} of {p.Name} to {r.EnvironmentName}.\nSource: {r.SourceReference}\n{(r.IsDirectToProd ? "This skips Staging. Reason: " + r.DirectToProdReason + "\n" : "")}" +
            (notify.Link(r.Id) is { } l ? $"\nOpen: {l}" : ""));

    // =========================== approve / reject / cancel / schedule ===========================

    private async Task<bool> SelfApprovalAllowedAsync(AppDbContext db, Guid projectId, string env) =>
        await db.ProjectEnvironmentSettings.AsNoTracking().Where(s => s.ProjectId == projectId && s.EnvironmentName == env).Select(s => s.AllowSelfApproval).FirstOrDefaultAsync();

    public async Task<OpResult> ApproveAsync(Actor actor, Guid requestId, string? comment = null)
    {
        await ApprovalGate.WaitAsync();
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            var req = await db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == requestId);
            if (req is null) return OpResult.Fail("Request not found.");
            var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
            var now = clock.GetUtcNow();
            if (await ExpireIfDueAsync(db, req, now)) return OpResult.Fail("This request has expired.");

            var holds = actor.Access.CanOnProject(ProjectPermission.ApproveDeployment, project.Id, req.EnvironmentName);
            var self = await SelfApprovalAllowedAsync(db, project.Id, req.EnvironmentName);
            var existing = await db.Approvals.AsNoTracking().Where(a => a.RequestId == requestId && a.Decision == ApprovalDecision.Approved).Select(a => a.ApproverId).ToListAsync();
            var check = ApprovalRules.Check(req.Status, req.RequestedById, actor.UserId, holds, self, existing, req.ApprovalsRequired);
            if (!check.Allowed)
            {
                await audit.WriteAsync(actor, new AuditRecord("Deployment.Approve", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName, Result: "Denied", Detail: check.Reason));
                return OpResult.Fail(check.Reason!);
            }

            var assignment = actor.Access.AssignmentsFor(ProjectPermission.ApproveDeployment, project.Id, req.EnvironmentName).FirstOrDefault();
            db.Approvals.Add(new Approval
            {
                Id = Guid.NewGuid(), RequestId = requestId, ApproverId = actor.UserId, ApproverName = actor.UserName, ApproverEmail = actor.Email,
                Decision = ApprovalDecision.Approved, Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), DecidedAt = now,
                IsSelfApproval = check.IsSelfApproval, AuthorizedByAssignmentId = assignment?.Id,
            });
            if (check.IsSelfApproval) req.SelfApproved = true;
            if (check.Completes)
            {
                var next = req.ScheduledAt is not null && req.ScheduledAt > now ? RequestStatus.Scheduled : RequestStatus.Approved;
                RequestStateMachine.EnsureCanTransition(req.Status, next);
                req.Status = next;
            }
            await db.SaveChangesAsync();
            await audit.WriteAsync(actor, new AuditRecord(check.IsSelfApproval ? "Deployment.Approve.Self" : "Deployment.Approve", "DeploymentRequest", requestId.ToString(),
                EnvironmentName: req.EnvironmentName,
                After: $"{project.Name}/{comp.Name}; {existing.Count + 1}/{req.ApprovalsRequired} approvals; requested by {req.RequestedBy}{(check.IsSelfApproval ? "; SELF-APPROVED" : "")}",
                Detail: comment));
            bus.Publish(new DeploymentChanged(requestId, null, false));
            return OpResult.Success();
        }
        finally { ApprovalGate.Release(); }
    }

    public async Task<OpResult> RejectAsync(Actor actor, Guid requestId, string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return OpResult.Fail("Write the reason for rejecting.");
        await ApprovalGate.WaitAsync();
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            var req = await db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == requestId);
            if (req is null) return OpResult.Fail("Request not found.");
            var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
            if (!actor.Access.CanOnProject(ProjectPermission.ApproveDeployment, project.Id, req.EnvironmentName))
            {
                await audit.WriteAsync(actor, new AuditRecord("Deployment.Reject", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName, Result: "Denied"));
                throw new ForbiddenException("You do not hold the Approver role for this project and environment.");
            }
            if (req.Status != RequestStatus.Submitted) return OpResult.Fail($"This request is {req.Status}, not waiting for approval.");
            db.Approvals.Add(new Approval
            {
                Id = Guid.NewGuid(), RequestId = requestId, ApproverId = actor.UserId, ApproverName = actor.UserName, ApproverEmail = actor.Email,
                Decision = ApprovalDecision.Rejected, Comment = comment.Trim(), DecidedAt = clock.GetUtcNow(),
                IsSelfApproval = string.Equals(req.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase),
            });
            RequestStateMachine.EnsureCanTransition(req.Status, RequestStatus.Rejected);
            req.Status = RequestStatus.Rejected; req.StatusDetail = comment.Trim(); req.EndedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();
            await audit.WriteAsync(actor, new AuditRecord("Deployment.Reject", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName, After: $"{project.Name}/{comp.Name}", Detail: comment));
            await Finished(req, project, comp, "rejected: " + comment.Trim());
            return OpResult.Success();
        }
        finally { ApprovalGate.Release(); }
    }

    public async Task<OpResult> CancelAsync(Actor actor, Guid requestId, string? reason)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var req = await db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return OpResult.Fail("Request not found.");
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
        var mine = string.Equals(req.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase);
        if (!mine && !actor.Can(Permission.ManageProjects))
        {
            await audit.WriteAsync(actor, new AuditRecord("Deployment.Cancel", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName, Result: "Denied"));
            throw new ForbiddenException("Only the person who submitted the request (or a Platform Admin) can cancel it.");
        }
        if (!RequestStateMachine.CanCancel(req.Status)) return OpResult.Fail($"A {req.Status} request cannot be cancelled.");
        req.Status = RequestStatus.Cancelled; req.StatusDetail = reason; req.EndedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Deployment.Cancel", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName, Detail: reason));
        await Finished(req, project, comp, "cancelled");
        return OpResult.Success();
    }

    public async Task<OpResult> RescheduleAsync(Actor actor, Guid requestId, DateTimeOffset? at)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var req = await db.DeploymentRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return OpResult.Fail("Request not found.");
        if (!string.Equals(req.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase) && !actor.Can(Permission.ManageProjects))
            throw new ForbiddenException("Only the person who submitted the request (or a Platform Admin) can change the schedule.");
        if (req.Status is not (RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Scheduled)) return OpResult.Fail($"A {req.Status} request cannot be rescheduled.");
        var now = clock.GetUtcNow();
        if (at is { } t && (t <= now.AddMinutes(1) || t >= req.ExpiresAt)) return OpResult.Fail("Pick a time in the future and before the request expires.");
        var before = req.ScheduledAt;
        req.ScheduledAt = at;
        if (req.Status is RequestStatus.Approved or RequestStatus.Scheduled)
            req.Status = at is not null ? RequestStatus.Scheduled : RequestStatus.Approved;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Deployment.Reschedule", "DeploymentRequest", requestId.ToString(), EnvironmentName: req.EnvironmentName,
            Before: before?.ToString("u"), After: at?.ToString("u") ?? "(as soon as approved)"));
        bus.Publish(new DeploymentChanged(requestId, null, false));
        return OpResult.Success();
    }

    /// <summary>Marks every open request that has passed its 7 days as Expired. Called by the maintenance job.</summary>
    public async Task<int> ExpireDueAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var due = await db.DeploymentRequests.Where(r => (r.Status == RequestStatus.Submitted || r.Status == RequestStatus.Approved || r.Status == RequestStatus.Scheduled || r.Status == RequestStatus.NeedsReapproval) && r.ExpiresAt < now).ToListAsync();
        foreach (var r in due) await ExpireIfDueAsync(db, r, now);
        return due.Count;
    }

    private async Task<bool> ExpireIfDueAsync(AppDbContext db, DeploymentRequest req, DateTimeOffset now)
    {
        if (req.ExpiresAt >= now || !RequestStateMachine.CanTransition(req.Status, RequestStatus.Expired)) return false;
        var was = req.Status;
        req.Status = RequestStatus.Expired; req.EndedAt = now; req.StatusDetail = "Not completed within 7 days.";
        await db.SaveChangesAsync();
        await audit.WriteSystemAsync(new AuditRecord("Deployment.Expire", "DeploymentRequest", req.Id.ToString(), EnvironmentName: req.EnvironmentName, Before: was.ToString(), After: "Expired"));
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
        await Finished(req, project, comp, "expired after 7 days");
        return true;
    }

    private async Task Finished(DeploymentRequest r, Project p, Component c, string what)
    {
        await notify.EnqueueAsync(r.Id, "Finished", [p.NotificationEmail, r.RequestedByEmail],
            $"[IIS Control Center] {p.Name}/{c.Name} → {r.EnvironmentName}: {what}",
            $"The request by {r.RequestedBy} for {c.Name} ({r.EnvironmentName}) is {what}." + (notify.Link(r.Id) is { } l ? $"\nOpen: {l}" : ""));
        bus.Publish(new DeploymentChanged(r.Id, null, false));
    }

    // =========================== manual rollback ===========================

    /// <summary>A rollback is a request like any other: Production needs approval. Only the latest successful run of a target can be rolled back.</summary>
    public async Task<OpResult<Guid>> RequestRollbackAsync(Actor actor, Guid runId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return OpResult<Guid>.Fail("Write the reason for the rollback.");
        await using var db = await dbf.CreateDbContextAsync();
        var run = await db.DeploymentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null) return OpResult<Guid>.Fail("Run not found.");
        var orig = await db.DeploymentRequests.AsNoTracking().FirstAsync(r => r.Id == run.RequestId);
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == orig.ComponentId);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
        if (!actor.Access.CanOnProject(ProjectPermission.SubmitDeployment, project.Id, orig.EnvironmentName))
        {
            await audit.WriteAsync(actor, new AuditRecord("Deployment.Rollback.Request", "DeploymentRun", runId.ToString(), EnvironmentName: orig.EnvironmentName, Result: "Denied"));
            throw new ForbiddenException("You do not hold the Deployer role for this project and environment.");
        }
        if (run.Status != RunStatus.Succeeded || string.IsNullOrEmpty(run.BackupId)) return OpResult<Guid>.Fail("Only a successful run that made a backup can be rolled back.");
        var newer = await db.DeploymentRuns.AnyAsync(r => r.DeploymentTargetId == run.DeploymentTargetId && r.Status == RunStatus.Succeeded && r.StartedAt > run.StartedAt);
        if (newer) return OpResult<Guid>.Fail("A newer deployment succeeded on this target. Only the latest deployment can be rolled back.");
        var open = await db.DeploymentRequests.AnyAsync(r => r.Type == RequestType.Rollback && r.RollbackOfRunId == runId && (r.Status == RequestStatus.Submitted || r.Status == RequestStatus.Approved || r.Status == RequestStatus.Scheduled || r.Status == RequestStatus.Running));
        if (open) return OpResult<Guid>.Fail("A rollback of this run is already open.");
        var target = await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == run.DeploymentTargetId);
        if (target.Status != TargetStatus.Active) return OpResult<Guid>.Fail($"The target is {target.Status}. Fix the link first.");

        var now = clock.GetUtcNow();
        var prod = orig.EnvironmentName == EnvironmentNames.Prod;
        var req = new DeploymentRequest
        {
            Id = Guid.NewGuid(), Type = RequestType.Rollback, ComponentId = comp.Id, EnvironmentName = orig.EnvironmentName, PackageId = orig.PackageId, Mode = orig.Mode,
            Notes = reason.Trim(), RollbackOfRunId = runId, Status = prod ? RequestStatus.Submitted : RequestStatus.Approved,
            StatusDetail = prod ? null : "No approval needed outside Production.",
            RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedByEmail = actor.Email, RequestedAt = now, ExpiresAt = now.Add(ApprovalRules.RequestLifetime),
            ApprovalsRequired = 1,
            SubmitAssignmentIds = string.Join(',', actor.Access.AssignmentsFor(ProjectPermission.SubmitDeployment, project.Id, orig.EnvironmentName).Select(a => a.Id)),
        };
        db.DeploymentRequests.Add(req);
        db.RequestTargets.Add(new RequestTarget { Id = Guid.NewGuid(), RequestId = req.Id, DeploymentTargetId = target.Id, DeployOrder = 0, PhysicalPathAtSubmit = target.PhysicalPath });
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Deployment.Rollback.Request", "DeploymentRequest", req.Id.ToString(), EnvironmentName: orig.EnvironmentName,
            After: $"{project.Name}/{comp.Name}; undo request {orig.Id}; backup={run.BackupId}", Detail: reason));
        if (prod) await NotifyNeedsApprovalAsync(req, project, comp, actor.UserName);
        bus.Publish(new DeploymentChanged(req.Id, null, false));
        return OpResult<Guid>.Success(req.Id);
    }

    // =========================== queries ===========================

    private static bool Sees(Actor a, Guid projectId, string env)
    {
        if (a.Can(Permission.ViewAllProjects) || a.Can(Permission.ManageProjects) || a.Access.IsProjectOwner(projectId)) return true;
        return a.Access.EnvironmentsFor(ProjectPermission.View, projectId).Contains(env);
    }

    private async Task<List<RequestRow>> RowsAsync(AppDbContext db, IQueryable<DeploymentRequest> q, Actor actor, int take)
    {
        var reqs = await q.AsNoTracking().OrderByDescending(r => r.RequestedAt).Take(take).ToListAsync();
        var cids = reqs.Select(r => r.ComponentId).Distinct().ToList();
        var comps = await db.Components.AsNoTracking().Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var pids = comps.Values.Select(c => c.ProjectId).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var rids = reqs.Select(r => r.Id).ToList();
        var pkgIds = reqs.Select(r => r.PackageId).Distinct().ToList();
        var pkgs = await db.Packages.AsNoTracking().Where(p => pkgIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FileName);
        var approvals = (await db.Approvals.AsNoTracking().Where(a => rids.Contains(a.RequestId) && a.Decision == ApprovalDecision.Approved).Select(a => a.RequestId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var tcounts = (await db.RequestTargets.AsNoTracking().Where(t => rids.Contains(t.RequestId)).Select(t => t.RequestId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<RequestRow>();
        foreach (var r in reqs)
        {
            var c = comps[r.ComponentId]; var p = projects[c.ProjectId];
            if (!Sees(actor, p.Id, r.EnvironmentName)) continue;
            rows.Add(new RequestRow(r.Id, r.Type, r.Status, p.Name, p.Id, c.Name, r.EnvironmentName, pkgs.GetValueOrDefault(r.PackageId, "?"), r.RequestedBy, r.RequestedAt,
                r.ExpiresAt, r.ScheduledAt, r.IsDirectToProd, r.SelfApproved, r.ApprovalsRequired, approvals.GetValueOrDefault(r.Id), tcounts.GetValueOrDefault(r.Id), r.SourceReference));
        }
        return rows;
    }

    /// <summary>Requests waiting for a decision that this person may decide.</summary>
    public async Task<IReadOnlyList<RequestRow>> ApprovalQueueAsync(Actor actor)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var rows = await RowsAsync(db, db.DeploymentRequests.Where(r => r.Status == RequestStatus.Submitted && r.ExpiresAt >= now), actor, 500);
        var mine = new List<RequestRow>();
        foreach (var r in rows)
        {
            if (!actor.Access.CanOnProject(ProjectPermission.ApproveDeployment, r.ProjectId, r.EnvironmentName)) continue;
            var already = await db.Approvals.AnyAsync(a => a.RequestId == r.Id && a.ApproverId == actor.UserId);
            if (!already) mine.Add(r);
        }
        return mine.OrderBy(r => r.RequestedAt).ToList();
    }

    public async Task<IReadOnlyList<RequestRow>> ListAsync(Actor actor, bool onlyMine = false, bool onlyOpen = false, Guid? projectId = null, int take = 200)
    {
        await using var db = await dbf.CreateDbContextAsync();
        IQueryable<DeploymentRequest> q = db.DeploymentRequests;
        if (onlyMine) q = q.Where(r => r.RequestedById == actor.UserId);
        if (onlyOpen) q = q.Where(r => r.Status == RequestStatus.Submitted || r.Status == RequestStatus.Approved || r.Status == RequestStatus.Scheduled || r.Status == RequestStatus.Running || r.Status == RequestStatus.NeedsReapproval);
        if (projectId is { } pid) q = q.Where(r => db.Components.Any(c => c.Id == r.ComponentId && c.ProjectId == pid));
        return await RowsAsync(db, q, actor, take);
    }

    public async Task<RequestDetail?> GetAsync(Actor actor, Guid requestId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var req = await db.DeploymentRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return null;
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);
        if (!Sees(actor, project.Id, req.EnvironmentName)) return null;
        var pkg = await db.Packages.AsNoTracking().FirstAsync(p => p.Id == req.PackageId);

        var rts = await db.RequestTargets.AsNoTracking().Where(t => t.RequestId == requestId).OrderBy(t => t.DeployOrder).ToListAsync();
        var tids = rts.Select(t => t.DeploymentTargetId).ToList();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var sids = targets.Values.Select(t => t.ServerId).Distinct().ToList();
        var servers = await db.Servers.AsNoTracking().Where(s => sids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        string Srv(Guid targetId) => targets.TryGetValue(targetId, out var t) ? servers.GetValueOrDefault(t.ServerId, "?") : "?";

        var td = rts.Select(rt => { var t = targets[rt.DeploymentTargetId]; return new TargetDetail(t.Id, Srv(t.Id), t.IisSiteName, t.AppPath, rt.PhysicalPathAtSubmit, rt.DeployOrder, rt.Added, rt.Replaced, rt.Deleted, rt.Skipped, rt.Unchanged, rt.DeletionWarning); }).ToList();
        var forced = (await db.ForcedFiles.AsNoTracking().Where(f => f.RequestId == requestId).ToListAsync()).Select(f =>
        {
            var per = new Dictionary<string, ForcedFileView>();
            if (!string.IsNullOrEmpty(f.MaskedDiffJson))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.MaskedDiffJson)!)
                {
                    string? Get(string n) => v.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    var b = Get("before"); var a = Get("after");
                    per[k] = new ForcedFileView(b, a, Get("note"), b is not null && a is not null ? TextDiff.Diff(b, a) is { } d ? TextDiff.Collapse(d) : null : null);
                }
            return new ForcedFileDetail(f.Path, f.Reason, per);
        }).ToList();
        var approvals = await db.Approvals.AsNoTracking().Where(a => a.RequestId == requestId).OrderBy(a => a.DecidedAt).ToListAsync();
        var runs = await db.DeploymentRuns.AsNoTracking().Where(r => r.RequestId == requestId).OrderBy(r => r.Sequence).ToListAsync();
        var rd = new List<RunDetail>();
        foreach (var r in runs)
        {
            var canRb = r.Status == RunStatus.Succeeded && !string.IsNullOrEmpty(r.BackupId) && req.Type == RequestType.Deploy
                        && actor.Access.CanOnProject(ProjectPermission.SubmitDeployment, project.Id, req.EnvironmentName)
                        && !await db.DeploymentRuns.AnyAsync(x => x.DeploymentTargetId == r.DeploymentTargetId && x.Status == RunStatus.Succeeded && x.StartedAt > r.StartedAt)
                        && !await db.DeploymentRequests.AnyAsync(x => x.Type == RequestType.Rollback && x.RollbackOfRunId == r.Id && (x.Status == RequestStatus.Submitted || x.Status == RequestStatus.Approved || x.Status == RequestStatus.Scheduled || x.Status == RequestStatus.Running || x.Status == RequestStatus.Succeeded));
            rd.Add(new RunDetail(r.Id, r.DeploymentTargetId, Srv(r.DeploymentTargetId), r.Sequence, r.Status, r.CurrentStep, r.StartedAt, r.EndedAt, r.FailureStep, r.Message,
                r.HealthDetail, r.BackupId, r.DurationMs, r.Added, r.Replaced, r.Deleted, r.Skipped, canRb));
        }

        string? banner = null;
        if (req.Type == RequestType.Deploy && runs.Count > 0 && rts.Count > 1)
        {
            var done = runs.Where(r => r.Status == RunStatus.Succeeded).Select(r => Srv(r.DeploymentTargetId)).ToList();
            var not = rts.Select(rt => Srv(rt.DeploymentTargetId)).Except(done).ToList();
            if (done.Count > 0 && not.Count > 0 && req.Status is RequestStatus.Failed or RequestStatus.RolledBack or RequestStatus.Running)
                banner = $"Partly deployed: succeeded on {string.Join(", ", done)}; not deployed on {string.Join(", ", not)}. The servers now run different versions.";
        }

        var selfAllowed = await SelfApprovalAllowedAsync(db, project.Id, req.EnvironmentName);
        var existing = approvals.Where(a => a.Decision == ApprovalDecision.Approved).Select(a => a.ApproverId).ToList();
        var chk = ApprovalRules.Check(req.Status, req.RequestedById, actor.UserId, actor.Access.CanOnProject(ProjectPermission.ApproveDeployment, project.Id, req.EnvironmentName), selfAllowed, existing, req.ApprovalsRequired);
        var mine = string.Equals(req.RequestedById, actor.UserId, StringComparison.OrdinalIgnoreCase) || actor.Can(Permission.ManageProjects);
        var warnings = pkg.WarningsJson is null ? new List<string>() : JsonSerializer.Deserialize<List<string>>(pkg.WarningsJson) ?? [];
        return new RequestDetail(req, project, comp, pkg, td, forced, approvals, rd, banner, chk.Allowed, chk.Allowed ? null : chk.Reason,
            mine && RequestStateMachine.CanCancel(req.Status), mine && req.Status is RequestStatus.Submitted or RequestStatus.Approved or RequestStatus.Scheduled, warnings, selfAllowed);
    }

    public async Task<IReadOnlyList<DeploymentLogLine>> GetLogAsync(Actor actor, Guid runId, int afterSeq = 0)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var run = await db.DeploymentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null) return [];
        var req = await db.DeploymentRequests.AsNoTracking().FirstAsync(r => r.Id == run.RequestId);
        var comp = await db.Components.AsNoTracking().FirstAsync(c => c.Id == req.ComponentId);
        if (!Sees(actor, comp.ProjectId, req.EnvironmentName)) return [];
        return await db.DeploymentLogLines.AsNoTracking().Where(l => l.RunId == runId && l.Seq > afterSeq).OrderBy(l => l.Seq).Take(2000).ToListAsync();
    }

    /// <summary>For the wizard: the packages a person uploaded for this component (their own, plus ones already used in a request they can see).</summary>
    public async Task<IReadOnlyList<Package>> PackagesAsync(Actor actor, Guid componentId, string environment)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var comp = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId);
        if (comp is null || !actor.Access.CanOnProject(ProjectPermission.SubmitDeployment, comp.ProjectId, environment)) return [];
        var now = clock.GetUtcNow();
        return await db.Packages.AsNoTracking().Where(p => p.ComponentId == componentId && p.DeletedAt == null && p.ExpiresAt > now).OrderByDescending(p => p.UploadedAt).Take(30).ToListAsync();
    }
}
