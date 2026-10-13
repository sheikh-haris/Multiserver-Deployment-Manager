using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using Microsoft.Extensions.Logging;

namespace IisControlCenter.Agent.Deploy;

public sealed class ExecutorOptions
{
    /// <summary>The agent's LOCAL allow-list (PathGuard + local roots). The portal cannot change it.</summary>
    public Func<string, bool> IsPathAllowed { get; init; } = _ => false;
    public int CopyRetries { get; init; } = 8;
    public TimeSpan CopyRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public double MinFreeSpaceFactor { get; init; } = 1.2;
    public Func<string, long>? FreeSpaceProbe { get; init; }
}

/// <summary>
/// Runs one deployment on this server, step by step, with a backup before any change and an automatic restore on failure.
/// Every decision about files comes from <see cref="DeployPlanner"/>, the same code the approver's diff used.
/// </summary>
public sealed class DeploymentExecutor(
    AgentDirs dirs,
    ExecutorOptions options,
    IPackageSource packages,
    IHealthChecker health,
    ISiteResolver sites,
    IPoolController pools,
    IAgentAntivirus antivirus,
    IRunPlanSource plans,
    ProtectedStore protectedStore,
    BackupStore backups,
    RunJournal journal,
    TimeProvider clock,
    ILogger<DeploymentExecutor> log)
{
    public const string OfflineFileName = "app_offline.htm";
    private const string OfflineHtml = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Updating</title></head><body><h1>Updating</h1><p>This site is being updated and will be back in a moment.</p></body></html>";

    // =====================================================================================================
    //  Deploy
    // =====================================================================================================

    public async Task<RunCompleted> DeployAsync(DeployCommand cmd, IRunReporter rep, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var runId = cmd.RunId;
        var staging = Path.Combine(dirs.Staging, runId.ToString("N"));
        string? backupId = null, backupPath = null;
        var failedStep = Steps.Validate;
        var weCreatedOffline = false;
        ResolvedApp? app = null;
        string target = cmd.PhysicalPath;
        var counts = (Added: 0, Replaced: 0, Deleted: 0, Skipped: 0);
        var madeChanges = false;
        ProtectedPaths pp = new([], []);

        void Log(string level, string step, string msg) { rep.Log(runId, level, step, msg); log.LogInformation("[{Run}] {Step}: {Msg}", runId.ToString("N")[..8], step, msg); }
        async Task<T> Step<T>(string step, Func<Task<T>> body)
        {
            failedStep = step; rep.Step(runId, step, "Started");
            try { var r = await body(); rep.Step(runId, step, "Done"); return r; }
            catch (Exception) { rep.Step(runId, step, "Failed"); throw; }
        }
        Task Step0(string step, Func<Task> body) => Step<int>(step, async () => { await body(); return 0; });

        var entry = new JournalEntry
        {
            RunId = runId, Kind = "Deploy", Phase = "Started", TargetPath = cmd.PhysicalPath, IisSiteId = cmd.IisSiteId, AppPath = cmd.AppPath,
            DataFolders = [.. cmd.ProtectedData], ConfigPatterns = [.. cmd.ProtectedConfig], HealthPath = cmd.HealthPath,
            ExpectedHealthStatus = cmd.ExpectedHealthStatus, StartedAt = clock.GetUtcNow(),
        };
        journal.Save(entry);

        try
        {
            // ---- Validate: the agent decides what is allowed, not the portal ----
            app = await Step(Steps.Validate, () => Task.FromResult(ValidateTarget(cmd.PhysicalPath, cmd.IisSiteId, cmd.AppPath, Log)));
            target = Path.GetFullPath(cmd.PhysicalPath);
            var nested = NestedFolders.RelativeInside(target, app.OtherIisFolders);
            pp = protectedStore.Merge(target, cmd.ProtectedData.Concat(nested), cmd.ProtectedConfig);
            if (nested.Count > 0) Log("Info", Steps.Validate, $"Other IIS applications or virtual directories live inside this folder and are left alone: {string.Join(", ", nested)}");
            entry.DataFolders = [.. pp.DataFolders]; entry.ConfigPatterns = [.. pp.ConfigPatterns];
            if (cmd.PackageSize <= 0 || string.IsNullOrWhiteSpace(cmd.PackageSha256)) throw new StepFailedException(Steps.Validate, "The command has no package size or hash.");
            Log("Info", Steps.Validate, $"Target {target}, app pool '{app.PoolName}', mode {cmd.Mode}.");

            // ---- Download + verify ----
            var zipPath = Path.Combine(staging, "package.zip");
            Directory.CreateDirectory(staging);
            await Step0(Steps.Download, async () =>
            {
                await packages.DownloadAsync(runId, cmd.PackageId, zipPath, ct);
                var info = new FileInfo(zipPath);
                if (!info.Exists || info.Length != cmd.PackageSize) throw new StepFailedException(Steps.Download, $"Downloaded {info.Length} bytes but expected {cmd.PackageSize}.");
                var hash = FolderScanner.Sha256File(zipPath);
                if (!hash.Equals(cmd.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new StepFailedException(Steps.Download, "The package hash does not match. The file was changed or damaged. Nothing was deployed.");
                Log("Info", Steps.Download, $"Package verified: {info.Length:N0} bytes, SHA-256 {hash[..16]}…");
            });

            if (cmd.ScanOnAgent)
                await Step0(Steps.Scan, async () =>
                {
                    var r = await antivirus.ScanAsync(zipPath, ct);
                    Log(r.Outcome == ScanOutcome.Clean ? "Info" : "Error", Steps.Scan, $"{r.Outcome}: {r.Detail}");
                    if (r.Outcome != ScanOutcome.Clean) throw new StepFailedException(Steps.Scan, r.Outcome == ScanOutcome.Infected ? "The antivirus found a threat in the package." : "The package could not be scanned on this server, so it was not deployed.");
                });

            // ---- Extract (and hash) ----
            var extractDir = Path.Combine(staging, "extract");
            var files = await Step(Steps.Extract, () => Task.FromResult(Extract(zipPath, extractDir, cmd.AllowedExtraExtensions, Log, ct)));

            // ---- Plan + compare with what the approver saw ----
            var planInput = await plans.GetAsync(runId, ct);
            DeployPlan plan = await Step(Steps.Snapshot, () => Task.FromResult(PlanAndCompare(cmd, planInput, target, pp, files, Log, ct)));
            counts = (plan.Count(FileAction.Add), plan.Count(FileAction.Replace) + plan.Count(FileAction.ForceOverwrite), plan.Count(FileAction.Delete),
                      plan.Count(FileAction.SkipProtectedConfig) + plan.Count(FileAction.SkipProtectedData));
            Log("Info", Steps.Snapshot, $"Plan: {counts.Added} added, {counts.Replaced} replaced, {counts.Deleted} deleted, {counts.Skipped} protected files skipped, {plan.Count(FileAction.Unchanged)} unchanged.");

            // ---- Backup ----
            var bk = await Step(Steps.Backup, () => Task.FromResult(MakeBackup(target, pp, runId, Log, ct)));
            backupId = bk.BackupId; backupPath = backups.PathOf(bk.BackupId);
            entry.BackupId = backupId; entry.Phase = "BackedUp"; journal.Save(entry);
            Log("Info", Steps.Backup, $"Backup {bk.BackupId} ({bk.SizeBytes:N0} bytes). Protected data folders are not in the backup.");

            // ---- Offline, copy, restart ----
            madeChanges = true;
            await Step0(Steps.Offline, () => { weCreatedOffline = PutOffline(target, Log); entry.WeCreatedOffline = weCreatedOffline; entry.Phase = "Offline"; journal.Save(entry); return Task.CompletedTask; });
            await Step0(Steps.Copy, () => { entry.Phase = "Copy"; journal.Save(entry); ApplyPlan(plan, target, extractDir, pp, Log, ct); return Task.CompletedTask; });
            await Step0(Steps.Restart, () => { entry.Phase = "Restart"; journal.Save(entry); Log("Info", Steps.Restart, pools.Restart(app.PoolName)); return Task.CompletedTask; });
            await Step0(Steps.Online, () => { RemoveOffline(target, weCreatedOffline, Log); entry.Phase = "Online"; journal.Save(entry); return Task.CompletedTask; });

            // ---- Health ----
            var hr = await Step(Steps.Health, async () =>
            {
                var r = await health.CheckAsync(app.Bindings, cmd.HealthPath, cmd.ExpectedHealthStatus, TimeSpan.FromSeconds(Math.Clamp(cmd.HealthTimeoutSeconds, 5, 600)), ct);
                Log(r.Ok ? "Info" : "Error", Steps.Health, r.Detail);
                if (!r.Ok) throw new StepFailedException(Steps.Health, "The health check failed: " + r.Detail);
                return r;
            });

            if (backupId is not null) { var n = backups.Prune(target, cmd.BackupsToKeep); if (n > 0) Log("Info", Steps.Cleanup, $"Removed {n} old backup(s). Keeping the newest {Math.Max(1, cmd.BackupsToKeep)}."); }
            entry.Phase = "Done"; journal.Save(entry);
            var done = new RunCompleted(runId, RunResults.Succeeded, backupId, backupPath, null, "Deployment succeeded.", counts.Added, counts.Replaced, counts.Deleted, counts.Skipped, sw.ElapsedMilliseconds, hr.Detail);
            return Finish(done);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The agent is stopping. The journal tells the next start to restore the backup.
            throw;
        }
        catch (Exception ex)
        {
            var step = ex is StepFailedException sf ? sf.Step : failedStep;
            var message = ex is StepFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            var resultKind = ex is StepFailedException s2 ? s2.Result : RunResults.Failed;
            Log("Error", step, message);

            if (!madeChanges || backupId is null)
            {
                if (weCreatedOffline) SafeRemoveOffline(target, Log);
                var c = new RunCompleted(runId, resultKind, backupId, backupPath, step, message + (resultKind == RunResults.NeedsReapproval ? "" : " No changes were made."), 0, 0, 0, 0, sw.ElapsedMilliseconds, null);
                return Finish(c);
            }

            // Changes may have started: put everything back.
            rep.Step(runId, Steps.Rollback, "Started");
            entry.Phase = "RollingBack"; journal.Save(entry);
            var restored = RestoreAndBringBack(target, backupId, pp, app, cmd.HealthPath, cmd.ExpectedHealthStatus, Log, ct, weCreatedOffline);
            rep.Step(runId, Steps.Rollback, restored.Ok ? "Done" : "Failed");
            var rc = new RunCompleted(runId, restored.Ok ? RunResults.RolledBack : RunResults.RollbackFailed, backupId, backupPath, step,
                restored.Ok ? $"{message} The previous version was restored automatically." : $"{message} THE AUTOMATIC RESTORE FAILED: {restored.Detail}. This server needs a person.",
                0, 0, 0, 0, sw.ElapsedMilliseconds, restored.Detail);
            return Finish(rc);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { /* cleaned up next time */ }
        }

        RunCompleted Finish(RunCompleted c) { journal.SaveCompleted(c); Log(c.Result == RunResults.Succeeded ? "Info" : "Error", Steps.Cleanup, $"Result: {c.Result}."); return c; }
    }

    // =====================================================================================================
    //  Manual rollback
    // =====================================================================================================

    public async Task<RunCompleted> RollbackAsync(RollbackCommand cmd, IRunReporter rep, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var runId = cmd.RunId;
        void Log(string level, string step, string msg) { rep.Log(runId, level, step, msg); log.LogInformation("[{Run}] {Step}: {Msg}", runId.ToString("N")[..8], step, msg); }
        string target = cmd.PhysicalPath;
        var step = Steps.Validate;
        try
        {
            rep.Step(runId, Steps.Validate, "Started");
            var app = ValidateTarget(cmd.PhysicalPath, cmd.IisSiteId, cmd.AppPath, Log);
            target = Path.GetFullPath(cmd.PhysicalPath);
            var bk = backups.Find(cmd.BackupId) ?? throw new StepFailedException(Steps.Validate, "This agent has no such backup. It may have been removed to save space.");
            if (!string.Equals(bk.TargetPath.TrimEnd('\\', '/'), target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new StepFailedException(Steps.Validate, "That backup belongs to a different folder.");
            var pp = protectedStore.Merge(target, NestedFolders.RelativeInside(target, app.OtherIisFolders), []);
            rep.Step(runId, Steps.Validate, "Done");

            var entry = new JournalEntry
            {
                RunId = runId, Kind = "Rollback", Phase = "RollingBack", TargetPath = target, IisSiteId = cmd.IisSiteId, AppPath = cmd.AppPath, BackupId = cmd.BackupId,
                DataFolders = [.. pp.DataFolders], ConfigPatterns = [.. pp.ConfigPatterns], HealthPath = cmd.HealthPath, ExpectedHealthStatus = cmd.ExpectedHealthStatus, StartedAt = clock.GetUtcNow(),
            };
            journal.Save(entry);
            step = Steps.Rollback; rep.Step(runId, Steps.Rollback, "Started");
            var r = RestoreAndBringBack(target, cmd.BackupId, pp, app, cmd.HealthPath, cmd.ExpectedHealthStatus, Log, ct);
            rep.Step(runId, Steps.Rollback, r.Ok ? "Done" : "Failed");
            var c = new RunCompleted(runId, r.Ok ? RunResults.Succeeded : RunResults.RollbackFailed, cmd.BackupId, backups.PathOf(cmd.BackupId), r.Ok ? null : Steps.Rollback,
                r.Ok ? "Rollback succeeded." : "The restore failed: " + r.Detail, 0, 0, 0, 0, sw.ElapsedMilliseconds, r.Detail);
            journal.SaveCompleted(c);
            return c;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = ex is StepFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            Log("Error", step, message);
            var c = new RunCompleted(runId, RunResults.Failed, cmd.BackupId, null, step, message, 0, 0, 0, 0, sw.ElapsedMilliseconds, null);
            journal.SaveCompleted(c);
            return c;
        }
    }

    // =====================================================================================================
    //  Crash recovery (called once when the agent starts)
    // =====================================================================================================

    public IReadOnlyList<RunCompleted> RecoverUnfinished(IRunReporter rep, CancellationToken ct)
    {
        var results = new List<RunCompleted>();
        foreach (var e in journal.Unfinished().ToList())
        {
            var runId = e.RunId;
            void Log(string level, string step, string msg) { rep.Log(runId, level, step, msg); log.LogWarning("[{Run}] recovery {Step}: {Msg}", runId.ToString("N")[..8], step, msg); }
            RunCompleted c;
            var restorePhases = new[] { "Offline", "Copy", "Restart", "Online", "RollingBack" };
            if (e.BackupId is not null && restorePhases.Contains(e.Phase))
            {
                try
                {
                    Log("Warning", Steps.Rollback, $"The agent stopped during '{e.Phase}'. Restoring backup {e.BackupId}.");
                    var pp = new ProtectedPaths(e.ConfigPatterns, e.DataFolders);
                    var app = sites.Resolve(e.IisSiteId, e.AppPath);
                    var r = RestoreAndBringBack(e.TargetPath, e.BackupId, pp, app, e.HealthPath, e.ExpectedHealthStatus, Log, ct, e.WeCreatedOffline);
                    c = new RunCompleted(runId, r.Ok ? (e.Kind == "Rollback" ? RunResults.Succeeded : RunResults.RolledBack) : RunResults.RollbackFailed, e.BackupId, backups.PathOf(e.BackupId), "Recovery",
                        r.Ok ? "The agent stopped while working. The previous version was restored when it started again." : "The agent stopped while working and the restore failed: " + r.Detail,
                        0, 0, 0, 0, 0, r.Detail);
                }
                catch (Exception ex) { c = new RunCompleted(runId, RunResults.RollbackFailed, e.BackupId, null, "Recovery", "Recovery failed: " + ex.Message, 0, 0, 0, 0, 0, null); }
            }
            else if (e.Phase == "Done")
            {
                c = new RunCompleted(runId, RunResults.Succeeded, e.BackupId, e.BackupId is null ? null : backups.PathOf(e.BackupId), null, "Deployment succeeded (result sent after a restart).", 0, 0, 0, 0, 0, null);
            }
            else
            {
                if (e.WeCreatedOffline) SafeRemoveOffline(e.TargetPath, Log);
                c = new RunCompleted(runId, RunResults.Failed, e.BackupId, null, "Recovery", "The agent stopped before any change was made.", 0, 0, 0, 0, 0, null);
            }
            journal.SaveCompleted(c);
            results.Add(c);
            try { var staging = Path.Combine(dirs.Staging, runId.ToString("N")); if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
        }
        return results;
    }

    // =====================================================================================================
    //  Steps
    // =====================================================================================================

    private ResolvedApp ValidateTarget(string physicalPath, long siteId, string appPath, Action<string, string, string> log) =>
        new TargetValidator(options, sites).Validate(physicalPath, siteId, appPath);

    private List<PackageFile> Extract(string zipPath, string dest, IReadOnlyList<string> allowedExtra, Action<string, string, string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        var extra = new HashSet<string>(allowedExtra.Select(x => x.ToLowerInvariant()));
        var blocked = new[] { ".exe", ".bat", ".cmd", ".ps1", ".msi" };
        var files = new List<PackageFile>();
        var seen = new HashSet<string>(RelativePaths.Comparer);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
            var p = RelativePaths.Normalize(e.FullName, out var err);
            if (p is null) throw new StepFailedException(Steps.Extract, $"Unsafe path '{e.FullName}' ({err}). Nothing was deployed.");
            if (!seen.Add(p)) throw new StepFailedException(Steps.Extract, $"Duplicate file '{p}'.");
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (blocked.Contains(ext) && !extra.Contains(ext)) throw new StepFailedException(Steps.Extract, $"File type {ext} is blocked for this component ('{p}').");
            if (p.Equals(OfflineFileName, StringComparison.OrdinalIgnoreCase)) throw new StepFailedException(Steps.Extract, $"The package must not contain {OfflineFileName}.");
            var outPath = Path.Combine(dest, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            using (var src = e.Open())
            using (var dst = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
            using (var sha = SHA256.Create())
            {
                var buf = new byte[81920]; int n; long total = 0;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, n); sha.TransformBlock(buf, 0, n, null, 0); total += n; }
                sha.TransformFinalBlock([], 0, 0);
                files.Add(new PackageFile(p, total, Convert.ToHexString(sha.Hash!)));
            }
        }
        if (files.Count == 0) throw new StepFailedException(Steps.Extract, "The package contains no files.");
        log("Info", Steps.Extract, $"Unpacked {files.Count} files to a temporary folder.");
        return files;
    }

    private DeployPlan PlanAndCompare(DeployCommand cmd, RunPlanInput input, string target, ProtectedPaths pp, List<PackageFile> files, Action<string, string, string> log, CancellationToken ct)
    {
        // The agent looks at the live folder itself. It does not trust a manifest from the portal.
        var live = new Dictionary<string, LiveFile>(RelativePaths.Comparer);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var l = FolderScanner.Probe(target, f.Path, hash: !pp.IsInDataFolder(f.Path));
            if (l is not null) live[f.Path] = l;
        }
        List<LiveFile>? all = null;
        if (cmd.Mode == DeployMode.FullReplace) all = FolderScanner.All(target, pp, hash: false);

        var forced = new HashSet<string>(cmd.ForcedPaths, RelativePaths.Comparer);
        var plan = DeployPlanner.Plan(files, live, all, cmd.Mode, pp, forced);

        // 1. Same live content for every package path as when the request was submitted?
        var snapshot = input.Snapshot.ToDictionary(s => s.Path, RelativePaths.Comparer);
        var differences = new List<string>();
        foreach (var f in files)
        {
            if (pp.IsInDataFolder(f.Path)) continue;                         // never compared: never touched
            snapshot.TryGetValue(f.Path, out var was);
            live.TryGetValue(f.Path, out var now);
            var wasExists = was?.Sha256 is not null; var nowExists = now is not null;
            if (wasExists != nowExists || (wasExists && !string.Equals(was!.Sha256, now!.Sha256, StringComparison.OrdinalIgnoreCase)))
                differences.Add(f.Path);
        }
        // 2. Full Replace: same list of files that will be deleted?
        if (cmd.Mode == DeployMode.FullReplace)
        {
            var approved = new HashSet<string>(input.Deletes, RelativePaths.Comparer);
            var actual = new HashSet<string>(plan.Deletes.Select(d => d.Path), RelativePaths.Comparer);
            // Only a deletion the approver did NOT see matters. Fewer deletions than approved is always safe
            // (a file vanished, or this agent protects more than the portal said).
            foreach (var p in actual.Except(approved, RelativePaths.Comparer)) differences.Add("(would delete) " + p);
        }
        if (differences.Count > 0)
        {
            var sample = string.Join(", ", differences.Take(5)) + (differences.Count > 5 ? $" and {differences.Count - 5} more" : "");
            throw new StepFailedException(Steps.Snapshot, $"The folder changed after the request was approved ({sample}). Nothing was deployed. Submit the request again so the approver sees the new state.", RunResults.NeedsReapproval);
        }
        return plan;
    }

    private BackupInfo MakeBackup(string target, ProtectedPaths pp, Guid runId, Action<string, string, string> log, CancellationToken ct)
    {
        var size = FolderScanner.TotalSize(target, pp);
        var free = options.FreeSpaceProbe?.Invoke(dirs.Backups) ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dirs.Backups))!).AvailableFreeSpace;
        if (free < size * options.MinFreeSpaceFactor)
            throw new StepFailedException(Steps.Backup, $"Not enough free disk space for the backup: need about {(long)(size * options.MinFreeSpaceFactor):N0} bytes, {free:N0} free. Nothing was changed.");
        return backups.Create(target, pp, runId, clock.GetUtcNow(), ct);
    }

    private bool PutOffline(string target, Action<string, string, string> log)
    {
        var file = Path.Combine(target, OfflineFileName);
        if (File.Exists(file)) { log("Warning", Steps.Offline, $"{OfflineFileName} already exists. Leaving it in place."); return false; }
        File.WriteAllText(file, OfflineHtml);
        log("Info", Steps.Offline, $"Placed {OfflineFileName} so the application releases its files.");
        return true;
    }

    private void RemoveOffline(string target, bool weCreated, Action<string, string, string> log)
    {
        var file = Path.Combine(target, OfflineFileName);
        if (weCreated && File.Exists(file)) { DeleteFile(file); log("Info", Steps.Online, $"Removed {OfflineFileName}."); }
    }

    private void SafeRemoveOffline(string target, Action<string, string, string> log)
    {
        try { var f = Path.Combine(target, OfflineFileName); if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { log("Warning", Steps.Cleanup, "Could not remove app_offline.htm: " + ex.Message); }
    }

    private void ApplyPlan(DeployPlan plan, string target, string extractDir, ProtectedPaths pp, Action<string, string, string> log, CancellationToken ct)
    {
        var written = 0; var deleted = 0;
        foreach (var c in plan.Writes)
        {
            ct.ThrowIfCancellationRequested();
            if (pp.IsInDataFolder(c.Path)) throw new StepFailedException(Steps.Copy, $"Refusing to write '{c.Path}': it is inside a protected data folder.");   // belt and braces
            var src = Path.Combine(extractDir, c.Path.Replace('/', Path.DirectorySeparatorChar));
            var dest = Path.Combine(target, c.Path.Replace('/', Path.DirectorySeparatorChar));
            EnsureSafeParent(target, dest);
            CopyWithRetry(src, dest, log);
            written++;
            if (c.Action == FileAction.ForceOverwrite) log("Warning", Steps.Copy, $"Force-overwrote protected config file {c.Path} as approved.");
        }
        foreach (var c in plan.Deletes)
        {
            ct.ThrowIfCancellationRequested();
            if (pp.IsInDataFolder(c.Path) || pp.IsConfigFile(c.Path)) throw new StepFailedException(Steps.Copy, $"Refusing to delete protected file '{c.Path}'.");
            var dest = Path.Combine(target, c.Path.Replace('/', Path.DirectorySeparatorChar));
            EnsureSafeParent(target, dest);
            DeleteFile(dest);
            deleted++;
        }
        if (plan.Deletes.Any()) PruneEmptyDirectories(target, pp);
        log("Info", Steps.Copy, $"Wrote {written} files, deleted {deleted} files.");
    }

    private (bool Ok, string Detail) RestoreAndBringBack(string target, string backupId, ProtectedPaths pp, ResolvedApp? app, string healthPath, int expected, Action<string, string, string> log, CancellationToken ct, bool offlineIsOurs = false)
    {
        var weCreated = offlineIsOurs;
        try
        {
            log("Warning", Steps.Rollback, $"Restoring backup {backupId}.");
            if (!weCreated) weCreated = PutOffline(target, log);
            backups.Restore(backupId, target, pp, m => log("Info", Steps.Rollback, m), ct);
            if (app is not null) log("Info", Steps.Rollback, pools.Restart(app.PoolName));
            RemoveOffline(target, weCreated, log);
            weCreated = false;
            if (app is null) return (true, "Restored. The site could not be looked up to run a health check.");
            var r = health.CheckAsync(app.Bindings, healthPath, expected, TimeSpan.FromSeconds(60), ct).GetAwaiter().GetResult();
            log(r.Ok ? "Info" : "Warning", Steps.Rollback, "Health after restore: " + r.Detail);
            return (true, r.Ok ? "Restored. " + r.Detail : "Restored, but the health check still fails: " + r.Detail);
        }
        catch (Exception ex)
        {
            log("Error", Steps.Rollback, $"Restore failed: {ex.GetType().Name}: {ex.Message}");
            if (weCreated) SafeRemoveOffline(target, log);
            return (false, ex.Message);
        }
    }

    // =====================================================================================================
    //  File helpers (static: also used by BackupStore)
    // =====================================================================================================

    /// <summary>Throws if the file is outside the target root, or any folder between the root and the file is a link, so a write can never leave the target.</summary>
    public static void EnsureSafeParent(string targetRoot, string destFile)
    {
        var root = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(destFile);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new StepFailedException(Steps.Copy, $"Refusing to write outside the target folder: '{destFile}'.");
        var dir = Path.GetDirectoryName(full)!;
        var current = root;
        foreach (var seg in Path.GetRelativePath(root, dir).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            current = Path.Combine(current, seg);
            if (Directory.Exists(current) && FolderScanner.IsReparsePoint(current))
                throw new StepFailedException(Steps.Copy, $"Refusing to write through a link: '{current}'.");
        }
        Directory.CreateDirectory(dir);
        if (File.Exists(full) && FolderScanner.IsReparsePoint(full))
            throw new StepFailedException(Steps.Copy, $"Refusing to overwrite a link: '{full}'.");
    }

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        var a = File.GetAttributes(path);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public static void PruneEmptyDirectories(string root, ProtectedPaths pp) => Prune(root, "", root, pp);

    private static bool Prune(string root, string rel, string dir, ProtectedPaths pp)
    {
        var empty = true;
        foreach (var sub in Directory.EnumerateDirectories(dir).ToList())
        {
            var r = rel.Length == 0 ? Path.GetFileName(sub) : rel + "/" + Path.GetFileName(sub);
            if (pp.IsDataFolderPath(r) || FolderScanner.IsReparsePoint(sub)) { empty = false; continue; }
            if (Prune(root, r, sub, pp)) { try { Directory.Delete(sub); } catch (IOException) { empty = false; } } else empty = false;
        }
        if (Directory.EnumerateFileSystemEntries(dir).Any()) empty = false;
        return empty && rel.Length > 0;
    }

    public static void WriteFromStream(string dest, ZipArchiveEntry entry)
    {
        var tmp = dest + ".icc-new";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var src = entry.Open())
                using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write)) src.CopyTo(dst);
                if (File.Exists(dest)) { var a = File.GetAttributes(dest); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, a & ~FileAttributes.ReadOnly); }
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt >= 8) throw;
                Thread.Sleep(500);
            }
        }
    }

    private void CopyWithRetry(string src, string dest, Action<string, string, string> log)
    {
        var tmp = dest + ".icc-new";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(src, tmp, overwrite: true);
                if (File.Exists(dest)) { var a = File.GetAttributes(dest); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, a & ~FileAttributes.ReadOnly); }
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt >= options.CopyRetries) throw new StepFailedException(Steps.Copy, $"Could not write '{Path.GetFileName(dest)}' after {attempt + 1} tries (file in use?): {ex.Message}");
                if (attempt == 0) log("Warning", Steps.Copy, $"'{Path.GetFileName(dest)}' is in use. Retrying.");
                Thread.Sleep(options.CopyRetryDelay);
            }
        }
    }
}
