using System.Threading.Channels;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using Microsoft.Extensions.Logging;

namespace IisControlCenter.Agent.Deploy;

/// <summary>How the coordinator talks to the portal. The worker implements it over SignalR and HTTPS.</summary>
public interface IPortalChannel
{
    Task SendStepAsync(RunStep step, CancellationToken ct);
    Task SendLogAsync(RunLog log, CancellationToken ct);
    /// <summary>Returns true when the portal stored the result (so the agent may forget it).</summary>
    Task<bool> SendCompletedAsync(RunCompleted result, CancellationToken ct);
    Task SendMaskedFileAsync(MaskedFileResult result, CancellationToken ct);
    Task UploadManifestAsync(ManifestResult result, CancellationToken ct);
    Task SendRejectedAsync(CommandRejected rejected, CancellationToken ct);
}

/// <summary>
/// Receives deployment-related commands, checks them against the LOCAL policy, runs them one at a time,
/// streams progress, and makes sure the final result reaches the portal even after a dropped connection or restart.
/// </summary>
public sealed class RunCoordinator(
    DeploymentExecutor executor,
    FolderInspector inspector,
    RunJournal journal,
    CommandPolicy policy,
    IPortalChannel channel,
    IManifestInputSource manifestInput,
    ILogger<RunCoordinator> log,
    TimeSpan? retryBaseDelay = null)
{
    private readonly TimeSpan _retryDelay = retryBaseDelay ?? TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _oneRunAtATime = new(1, 1);

    // ---------- commands ----------

    public async Task HandleManifestAsync(CommandEnvelope e, ManifestRequest req, CancellationToken ct)
    {
        if (await RejectIfRefusedAsync(e, ct)) return;
        var paths = await manifestInput.GetPathsAsync(e.CommandId, ct);
        var result = await Task.Run(() => inspector.GetManifest(e.CommandId, req, paths), ct);
        await channel.UploadManifestAsync(result, ct);
    }

    public async Task HandleMaskedFileAsync(CommandEnvelope e, MaskedFileRequest req, CancellationToken ct)
    {
        if (await RejectIfRefusedAsync(e, ct)) return;
        var result = await Task.Run(() => inspector.GetMaskedFile(e.CommandId, req), ct);
        await channel.SendMaskedFileAsync(result, ct);
    }

    public async Task HandleDeployAsync(CommandEnvelope e, DeployCommand cmd, CancellationToken ct)
    {
        if (await RejectIfRefusedAsync(e, ct)) return;
        if (await AlreadyKnownAsync(cmd.RunId, ct)) return;
        await RunSerializedAsync(cmd.RunId, rep => executor.DeployAsync(cmd, rep, ct), ct);
    }

    public async Task HandleRollbackAsync(CommandEnvelope e, RollbackCommand cmd, CancellationToken ct)
    {
        if (await RejectIfRefusedAsync(e, ct)) return;
        if (await AlreadyKnownAsync(cmd.RunId, ct)) return;
        await RunSerializedAsync(cmd.RunId, rep => executor.RollbackAsync(cmd, rep, ct), ct);
    }

    // ---------- startup and reconnect ----------

    /// <summary>Called once when the agent starts: undo any run that was cut off.</summary>
    public void RecoverAfterRestart(CancellationToken ct)
    {
        using var rep = new Reporter(channel, log, default);
        var recovered = executor.RecoverUnfinished(rep, ct);
        foreach (var c in recovered) log.LogWarning("Recovered run {Run}: {Result}", c.RunId, c.Result);
    }

    /// <summary>Called after every (re)connect: send every result the portal has not confirmed yet.</summary>
    public async Task ResendUnconfirmedAsync(CancellationToken ct)
    {
        foreach (var c in journal.AllCompleted().ToList())
        {
            try { if (await channel.SendCompletedAsync(c, ct)) journal.Acknowledge(c.RunId); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Could not resend result of run {Run}: {Message}", c.RunId, ex.Message); }
        }
    }

    // ---------- internals ----------

    private async Task<bool> RejectIfRefusedAsync(CommandEnvelope e, CancellationToken ct)
    {
        var refusal = policy.Validate(e);
        if (refusal is null) return false;
        log.LogWarning("Refused command {Id}: {Reason}", e.CommandId, refusal);
        await channel.SendRejectedAsync(new CommandRejected(e.CommandId, refusal), ct);
        return true;
    }

    /// <summary>The same run id again (a retry after a lost connection) must not run twice: just report what is known.</summary>
    private async Task<bool> AlreadyKnownAsync(Guid runId, CancellationToken ct)
    {
        var done = journal.LoadCompleted(runId);
        if (done is not null) { if (await channel.SendCompletedAsync(done, ct)) journal.Acknowledge(runId); return true; }
        return journal.Load(runId) is not null;     // running or being recovered: the result will arrive
    }

    private async Task RunSerializedAsync(Guid runId, Func<IRunReporter, Task<RunCompleted>> run, CancellationToken ct)
    {
        await _oneRunAtATime.WaitAsync(ct);
        try
        {
            using var rep = new Reporter(channel, log, ct);
            RunCompleted result;
            try { result = await run(rep); }
            finally { await rep.FlushAsync(); }

            for (var attempt = 1; ; attempt++)
            {
                try { if (await channel.SendCompletedAsync(result, ct)) journal.Acknowledge(runId); break; }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < 5)
                {
                    log.LogWarning("Could not send the result yet ({Message}). It is saved and will be sent again.", ex.Message);
                    await Task.Delay(_retryDelay * attempt, ct);
                }
                catch (Exception) { break; }    // stays on disk: ResendUnconfirmedAsync sends it after the next connect
            }
        }
        finally { _oneRunAtATime.Release(); }
    }

    /// <summary>Sends step and log messages to the portal in the background, in batches. Live logs are best effort; the result is durable.</summary>
    private sealed class Reporter : IRunReporter, IDisposable
    {
        private readonly Channel<object> _queue = Channel.CreateUnbounded<object>();
        private readonly Task _pump;
        private readonly IPortalChannel _channel;
        private readonly ILogger _log;

        public Reporter(IPortalChannel channel, ILogger log, CancellationToken ct)
        {
            _channel = channel; _log = log;
            _pump = Task.Run(() => PumpAsync(ct));
        }

        public void Step(Guid runId, string step, string status, string? detail = null) => _queue.Writer.TryWrite(new RunStep(runId, step, status, detail));
        public void Log(Guid runId, string level, string step, string message) => _queue.Writer.TryWrite(new LogItem(runId, new RunLogLine(DateTimeOffset.UtcNow, level, step, message)));

        private sealed record LogItem(Guid RunId, RunLogLine Line);

        public async Task FlushAsync() { _queue.Writer.TryComplete(); await _pump; }

        private async Task PumpAsync(CancellationToken ct)
        {
            var batch = new List<LogItem>();
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(CancellationToken.None))
                {
                    if (item is LogItem li)
                    {
                        batch.Add(li);
                        if (batch.Count >= 20 || _queue.Reader.Count == 0) await SendBatchAsync(batch, ct);
                    }
                    else if (item is RunStep s)
                    {
                        await SendBatchAsync(batch, ct);     // keep order: logs before the step marker
                        try { await _channel.SendStepAsync(s, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug("Step not sent: {Message}", ex.Message); }
                    }
                }
                await SendBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) { }
        }

        private async Task SendBatchAsync(List<LogItem> batch, CancellationToken ct)
        {
            if (batch.Count == 0) return;
            foreach (var g in batch.GroupBy(b => b.RunId))
            {
                try { await _channel.SendLogAsync(new RunLog(g.Key, g.Select(x => x.Line).ToList()), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug("Log lines not sent: {Message}", ex.Message); }
            }
            batch.Clear();
        }

        public void Dispose() => _queue.Writer.TryComplete();
    }
}
