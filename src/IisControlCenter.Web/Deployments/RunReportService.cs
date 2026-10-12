using IisControlCenter.Contracts;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// Takes what the agent reports about a run. Only the agent of the server that owns the run may report on it.
/// The final result is idempotent: the agent keeps sending it until the portal confirms, so a repeat must change nothing.
/// </summary>
public sealed class RunReportService(
    IDbContextFactory<AppDbContext> dbf,
    DeploymentOrchestrator orchestrator,
    AuditService audit,
    DeploymentEventBus bus,
    TimeProvider clock)
{
    public const int MaxLogLinesPerRun = 5000;

    private async Task<(DeploymentRun run, Guid serverId)?> OwnedAsync(AppDbContext db, Guid runId, Guid serverId)
    {
        var run = await db.DeploymentRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null) return null;
        var owner = await db.DeploymentTargets.AsNoTracking().Where(t => t.Id == run.DeploymentTargetId).Select(t => t.ServerId).FirstAsync();
        return owner == serverId ? (run, owner) : null;
    }

    public async Task StepAsync(Guid serverId, RunStep s)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var o = await OwnedAsync(db, s.RunId, serverId);
        if (o is null || o.Value.run.Status != RunStatus.Running) return;
        o.Value.run.CurrentStep = Clip(s.Step, 100);
        await db.SaveChangesAsync();
        bus.Publish(new DeploymentChanged(o.Value.run.RequestId, s.RunId, false));
    }

    public async Task LogAsync(Guid serverId, RunLog log)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var o = await OwnedAsync(db, log.RunId, serverId);
        if (o is null) return;
        var last = await db.DeploymentLogLines.Where(l => l.RunId == log.RunId).MaxAsync(l => (int?)l.Seq) ?? 0;
        if (last >= MaxLogLinesPerRun) return;
        foreach (var line in log.Lines.Take(500))
        {
            if (++last > MaxLogLinesPerRun) break;
            db.DeploymentLogLines.Add(new DeploymentLogLine { RunId = log.RunId, Seq = last, At = line.At, Level = Clip(line.Level, 10), Step = Clip(line.Step, 100), Message = Clip(line.Message, 2000) });
        }
        await db.SaveChangesAsync();
        bus.Publish(new DeploymentChanged(o.Value.run.RequestId, log.RunId, true));
    }

    /// <summary>Returns true when the result is stored (the agent may then forget it).</summary>
    public async Task<bool> CompletedAsync(Guid serverId, RunCompleted c)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var o = await OwnedAsync(db, c.RunId, serverId);
        if (o is null) return true;      // not ours or unknown: tell the agent to forget it, there is nothing to store
        var run = o.Value.run;
        // Already stored? A run we gave up on (timeout, send failure) may still deliver a late result: accept it, the real outcome matters more.
        if (run.EndedAt is not null && run.FailureStep is not ("Timeout" or "Send")) return true;
        var req = await db.DeploymentRequests.FirstAsync(r => r.Id == run.RequestId);
        var comp = await db.Components.AsNoTracking().FirstAsync(x => x.Id == req.ComponentId);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == comp.ProjectId);

        run.Status = c.Result switch
        {
            RunResults.Succeeded => RunStatus.Succeeded,
            RunResults.RolledBack => RunStatus.RolledBack,
            RunResults.NeedsReapproval => RunStatus.NeedsReapproval,
            _ => RunStatus.Failed,
        };
        run.EndedAt = clock.GetUtcNow(); run.BackupId = c.BackupId; run.BackupPath = c.BackupPath; run.FailureStep = c.FailureStep;
        run.Message = c.Result == RunResults.RollbackFailed ? "ROLLBACK FAILED — the site may be broken and needs a person. " + c.Message : c.Message;
        run.HealthDetail = c.HealthDetail; run.Added = c.Added; run.Replaced = c.Replaced; run.Deleted = c.Deleted; run.Skipped = c.Skipped; run.DurationMs = c.DurationMs;
        run.CurrentStep = null;
        await db.SaveChangesAsync();

        await audit.WriteSystemAsync(new AuditRecord("Deployment.Run.Result", "DeploymentRun", run.Id.ToString(), serverId, req.EnvironmentName,
            After: $"{project.Name}/{comp.Name}: {c.Result}; +{c.Added} ~{c.Replaced} -{c.Deleted} skipped {c.Skipped}; {c.DurationMs} ms; backup={c.BackupId}",
            Result: c.Result == RunResults.Succeeded ? "Success" : "Failed", Detail: c.Message));

        if (req.Type == RequestType.Rollback && run.Status == RunStatus.Succeeded && req.RollbackOfRunId is { } orig)
        {
            var o2 = await db.DeploymentRuns.FirstOrDefaultAsync(r => r.Id == orig);
            if (o2 is not null) { o2.Status = RunStatus.RolledBack; o2.Message = (o2.Message is null ? "" : o2.Message + " ") + "Undone by a manual rollback."; await db.SaveChangesAsync(); }
        }

        if (req.Status == RequestStatus.Running)
        {
            switch (run.Status)
            {
                case RunStatus.Succeeded: await orchestrator.AdvanceAsync(req.Id); break;     // starts the next target or finishes
                case RunStatus.RolledBack: await orchestrator.FinishAsync(db, req, project, comp, RequestStatus.RolledBack, run.Message, default); break;
                case RunStatus.NeedsReapproval: await orchestrator.FinishAsync(db, req, project, comp, RequestStatus.NeedsReapproval, run.Message, default); break;
                default: await orchestrator.FinishAsync(db, req, project, comp, RequestStatus.Failed, run.Message, default); break;
            }
        }
        bus.Publish(new DeploymentChanged(req.Id, run.Id, false));
        return true;
    }

    private static string Clip(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
