using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Deploy;
using IisControlCenter.Agent.Security;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using Microsoft.Extensions.Logging.Abstractions;

namespace IisControlCenter.Agent.Tests;

public sealed class FakeChannel : IPortalChannel
{
    public List<RunStep> Steps { get; } = [];
    public List<RunLog> Logs { get; } = [];
    public List<RunCompleted> Completed { get; } = [];
    public List<MaskedFileResult> Masked { get; } = [];
    public List<ManifestResult> Manifests { get; } = [];
    public List<CommandRejected> Rejected { get; } = [];
    public bool Online { get; set; } = true;
    public bool Ack { get; set; } = true;
    public Task SendStepAsync(RunStep s, CancellationToken ct) { lock (Steps) Steps.Add(s); return Task.CompletedTask; }
    public Task SendLogAsync(RunLog l, CancellationToken ct) { lock (Logs) Logs.Add(l); return Task.CompletedTask; }
    public Task<bool> SendCompletedAsync(RunCompleted r, CancellationToken ct) { if (!Online) throw new InvalidOperationException("offline"); Completed.Add(r); return Task.FromResult(Ack); }
    public Task SendMaskedFileAsync(MaskedFileResult r, CancellationToken ct) { Masked.Add(r); return Task.CompletedTask; }
    public Task UploadManifestAsync(ManifestResult r, CancellationToken ct) { Manifests.Add(r); return Task.CompletedTask; }
    public Task SendRejectedAsync(CommandRejected r, CancellationToken ct) { Rejected.Add(r); return Task.CompletedTask; }
}

public class RunCoordinatorTests : IDisposable
{
    private readonly DeployHarness h = new();
    private readonly FakeChannel ch = new();
    private readonly Guid serverId = Guid.NewGuid();
    private readonly RunCoordinator rc;
    public void Dispose() => h.Dispose();

    public RunCoordinatorTests()
    {
        var cfg = new AgentConfig { PortalUrl = "https://agents.local", Environment = "Dev", AllowedRoots = [@"d:\websites"], ServerId = serverId };
        rc = new RunCoordinator(h.Executor, h.Inspector, h.Journal, new CommandPolicy(cfg, h.Clock), ch, h.Plans, NullLogger<RunCoordinator>.Instance, TimeSpan.FromMilliseconds(5));
    }

    private CommandEnvelope Env(string env = "Dev", Guid? server = null) =>
        new(Guid.NewGuid(), server ?? serverId, env, h.Clock.GetUtcNow(), h.Clock.GetUtcNow().AddMinutes(5), "c", ServerMode.ReadOnly);

    [Fact]
    public async Task A_deploy_runs_streams_progress_and_sends_the_result_which_is_then_forgotten_once_confirmed()
    {
        h.Live("index.html", "v1");
        var cmd = h.Command(DeployHarness.Zip(("index.html", "v2")));
        await rc.HandleDeployAsync(Env(), cmd, default);
        Assert.Equal("v2", h.Read("index.html"));
        var done = Assert.Single(ch.Completed);
        Assert.Equal((cmd.RunId, RunResults.Succeeded), (done.RunId, done.Result));
        Assert.Contains(ch.Steps, s => s.Step == "Copy" && s.Status == "Done");
        Assert.Contains(ch.Logs.SelectMany(l => l.Lines), l => l.Step == "Backup");
        Assert.Empty(h.Journal.AllCompleted());                                  // acknowledged: nothing left to resend
    }

    [Fact]
    public async Task A_command_for_another_environment_or_server_is_refused_and_nothing_runs()
    {
        h.Live("index.html", "v1");
        await rc.HandleDeployAsync(Env("Prod"), h.Command(DeployHarness.Zip(("index.html", "v2"))), default);
        await rc.HandleDeployAsync(Env(server: Guid.NewGuid()), h.Command(DeployHarness.Zip(("index.html", "v2"))), default);
        await rc.HandleManifestAsync(Env("Prod"), new ManifestRequest(h.Target, 1, "/", false, []), default);
        await rc.HandleMaskedFileAsync(Env("Prod"), new MaskedFileRequest(h.Target, 1, "/", "web.config", "s", ["web.config"]), default);
        Assert.Equal(4, ch.Rejected.Count);
        Assert.Equal("v1", h.Read("index.html"));
        Assert.Empty(ch.Completed); Assert.Empty(ch.Manifests); Assert.Empty(ch.Masked);
    }

    [Fact]
    public async Task A_replayed_command_id_is_refused()
    {
        h.Live("index.html", "v1");
        var env = Env();
        var cmd = h.Command(DeployHarness.Zip(("index.html", "v2")));
        await rc.HandleDeployAsync(env, cmd, default);
        await rc.HandleDeployAsync(env, cmd with { RunId = Guid.NewGuid() }, default);
        Assert.Single(ch.Rejected);
        Assert.Contains("Duplicate", ch.Rejected[0].Reason);
    }

    [Fact]
    public async Task The_same_run_id_sent_again_after_a_lost_connection_does_not_deploy_twice_it_resends_the_result()
    {
        h.Live("index.html", "v1");
        ch.Ack = false;                                                           // the portal did not confirm
        var cmd = h.Command(DeployHarness.Zip(("index.html", "v2")));
        await rc.HandleDeployAsync(Env(), cmd, default);
        Assert.Single(ch.Completed);
        h.Live("index.html", "someone changed it later");
        await rc.HandleDeployAsync(Env(), cmd, default);                          // the portal retries the same run
        Assert.Equal(2, ch.Completed.Count);                                      // result sent again
        Assert.Equal("someone changed it later", h.Read("index.html"));           // NOT deployed again
        Assert.Equal(1, h.Pools.Restarted.Count);
    }

    [Fact]
    public async Task A_result_that_could_not_be_sent_is_kept_and_resent_after_reconnect()
    {
        h.Live("index.html", "v1");
        ch.Online = false;
        var cmd = h.Command(DeployHarness.Zip(("index.html", "v2")));
        var task = rc.HandleDeployAsync(Env(), cmd, default);
        // The coordinator retries with a short delay; cancel its waiting by letting it exhaust quickly.
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Equal("v2", h.Read("index.html"));                                 // the deployment itself finished
        Assert.NotNull(h.Journal.LoadCompleted(cmd.RunId));                       // result saved
        ch.Online = true;
        await rc.ResendUnconfirmedAsync(default);
        Assert.Single(ch.Completed);
        Assert.Empty(h.Journal.AllCompleted());
    }

    [Fact]
    public async Task Only_one_deployment_runs_at_a_time()
    {
        h.Live("index.html", "v1");
        var gate = new TaskCompletionSource();
        var running = 0; var maxRunning = 0;
        h.Packages.Before = async () => { var n = Interlocked.Increment(ref running); maxRunning = Math.Max(maxRunning, n); await Task.Delay(100); Interlocked.Decrement(ref running); };
        var a = rc.HandleDeployAsync(Env(), h.Command(DeployHarness.Zip(("a.txt", "a"))), default);
        var b = rc.HandleDeployAsync(Env(), h.Command(DeployHarness.Zip(("b.txt", "b"))), default);
        await Task.WhenAll(a, b);
        Assert.Equal(1, maxRunning);
        Assert.Equal(2, ch.Completed.Count);
    }

    [Fact]
    public async Task Manifest_and_masked_file_answers_go_back_through_the_channel()
    {
        h.Live("index.html", "x"); h.Live("web.config", "<a password=\"p\"/>");
        h.Plans.ManifestPaths = ["index.html"];
        await rc.HandleManifestAsync(Env(), new ManifestRequest(h.Target, 1, "/", false, ["uploads"]), default);
        await rc.HandleMaskedFileAsync(Env(), new MaskedFileRequest(h.Target, 1, "/", "web.config", "s", ["web.config"]), default);
        Assert.Single(ch.Manifests[0].Existing);
        Assert.DoesNotContain("password=\"p\"", ch.Masked[0].MaskedText);
    }

    [Fact]
    public async Task Startup_recovery_restores_a_cut_off_run_and_the_result_is_sent_after_connecting()
    {
        h.Live("index.html", "v1");
        var pp = new ProtectedPaths(["web.config"], ["uploads"]);
        var runId = Guid.NewGuid();
        var bk = h.Backups.Create(h.Target, pp, runId, h.Clock.GetUtcNow(), default);
        h.Live("index.html", "half written");
        h.Journal.Save(new JournalEntry { RunId = runId, Phase = "Copy", TargetPath = h.Target, IisSiteId = 1, AppPath = "/", BackupId = bk.BackupId, DataFolders = ["uploads"], ConfigPatterns = ["web.config"], StartedAt = h.Clock.GetUtcNow() });
        rc.RecoverAfterRestart(default);
        Assert.Equal("v1", h.Read("index.html"));
        await rc.ResendUnconfirmedAsync(default);
        var r = Assert.Single(ch.Completed);
        Assert.Equal((runId, RunResults.RolledBack), (r.RunId, r.Result));
    }
}
