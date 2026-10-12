using IisControlCenter.Agent.Deploy;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Agent.Tests;

public class ManualRollbackTests : IDisposable
{
    private readonly DeployHarness h = new();
    public void Dispose() => h.Dispose();

    private RollbackCommand Rb(string backupId) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), h.Target, 1, "/", backupId, "/health", 200, 10);

    [Fact]
    public async Task Rollback_restores_the_version_before_the_deployment_and_keeps_user_data()
    {
        h.Live("index.html", "v1"); h.Live("uploads/u.png", "DATA"); h.Live("only-v1.txt", "gone in v2");
        var deploy = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v2"), ("new.txt", "n")), DeployMode.FullReplace));
        Assert.Equal(RunResults.Succeeded, deploy.Result);
        Assert.Equal("v2", h.Read("index.html"));
        h.Live("uploads/u2.png", "DATA 2 uploaded after the deployment");

        var rep = new CollectingReporter();
        var r = await h.Executor.RollbackAsync(Rb(deploy.BackupId!), rep, default);
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("v1", h.Read("index.html"));
        Assert.Equal("gone in v2", h.Read("only-v1.txt"));
        Assert.False(h.Exists("new.txt"));
        Assert.Equal("DATA", h.Read("uploads/u.png"));
        Assert.Equal("DATA 2 uploaded after the deployment", h.Read("uploads/u2.png"));
        Assert.False(h.Exists("app_offline.htm"));
        Assert.True(rep.Reached(Steps.Rollback));
    }

    [Fact]
    public async Task Rollback_refuses_unknown_backups_and_backups_of_another_folder()
    {
        h.Live("index.html", "v1");
        var d = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v2"))));
        var unknown = await h.Executor.RollbackAsync(Rb("20200101000000-deadbeef"), new CollectingReporter(), default);
        Assert.Equal(RunResults.Failed, unknown.Result);
        Assert.Contains("no such backup", unknown.Message);

        // A backup that exists but belongs to a different folder must not be restored here.
        var other = Path.Combine(h.Root, "sites", "billing", "web"); Directory.CreateDirectory(other);
        h.Sites.Path = other;
        var wrong = await h.Executor.RollbackAsync(Rb(d.BackupId!) with { PhysicalPath = other }, new CollectingReporter(), default);
        Assert.Equal(RunResults.Failed, wrong.Result);
        Assert.Contains("different folder", wrong.Message);
        Assert.Empty(Directory.GetFileSystemEntries(other));
    }

    [Fact]
    public async Task Rollback_is_refused_for_a_folder_outside_the_allowed_roots()
    {
        h.Live("index.html", "v1");
        var d = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v2"))));
        var outside = Path.Combine(h.Root, "elsewhere"); Directory.CreateDirectory(outside); h.Sites.Path = outside;
        var r = await h.Executor.RollbackAsync(Rb(d.BackupId!) with { PhysicalPath = outside }, new CollectingReporter(), default);
        Assert.Equal(RunResults.Failed, r.Result);
        Assert.Contains("outside the folders", r.Message);
    }

    [Fact]
    public async Task A_failed_restore_is_reported_as_rollback_failed()
    {
        h.Live("index.html", "v1");
        var d = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v2"))));
        h.Pools.ShouldFail = _ => true;
        var r = await h.Executor.RollbackAsync(Rb(d.BackupId!), new CollectingReporter(), default);
        Assert.Equal(RunResults.RollbackFailed, r.Result);
    }
}

public class RestoreBehaviourTests : IDisposable
{
    private readonly DeployHarness h = new();
    public void Dispose() => h.Dispose();

    [Fact]
    public async Task Restore_leaves_files_that_already_match_the_backup_untouched()
    {
        h.Live("same.txt", "same"); h.Live("changed.txt", "old");
        var path = Path.Combine(h.Target, "same.txt");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var pp = new ProtectedPaths([], []);
        var bk = h.Backups.Create(h.Target, pp, Guid.NewGuid(), h.Clock.GetUtcNow(), default);
        h.Live("changed.txt", "NEW CONTENT");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        h.Backups.Restore(bk.BackupId, h.Target, pp, null, default);
        Assert.Equal("old", h.Read("changed.txt"));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));   // not rewritten
        await Task.CompletedTask;
    }
}

public class CrashRecoveryTests : IDisposable
{
    private readonly DeployHarness h = new();
    public void Dispose() => h.Dispose();

    /// <summary>Simulates an agent that died half-way through copying: backup exists, files half replaced, journal says "Copy".</summary>
    private Guid Crash(string phase = "Copy", bool offline = true)
    {
        h.Live("index.html", "v1"); h.Live("bin/App.dll", "dll v1"); h.Live("uploads/u.png", "DATA");
        var pp = new ProtectedPaths(["web.config"], ["uploads"]);
        var runId = Guid.NewGuid();
        var bk = h.Backups.Create(h.Target, pp, runId, h.Clock.GetUtcNow(), default);
        h.Live("index.html", "v2 HALF WRITTEN"); h.Live("junk-from-new-version.txt", "x");
        if (offline) h.Live("app_offline.htm", "offline");
        h.Journal.Save(new JournalEntry
        {
            RunId = runId, Kind = "Deploy", Phase = phase, TargetPath = h.Target, IisSiteId = 1, AppPath = "/", BackupId = bk.BackupId,
            DataFolders = ["uploads"], ConfigPatterns = ["web.config"], HealthPath = "/health", WeCreatedOffline = offline, StartedAt = h.Clock.GetUtcNow(),
        });
        return runId;
    }

    [Theory]
    [InlineData("Offline")] [InlineData("Copy")] [InlineData("Restart")] [InlineData("Online")] [InlineData("RollingBack")]
    public void A_run_that_died_after_the_backup_is_rolled_back_when_the_agent_starts(string phase)
    {
        var id = Crash(phase);
        var results = h.Executor.RecoverUnfinished(new CollectingReporter(), default);
        var c = Assert.Single(results);
        Assert.Equal((id, RunResults.RolledBack), (c.RunId, c.Result));
        Assert.Equal("v1", h.Read("index.html"));
        Assert.False(h.Exists("junk-from-new-version.txt"));
        Assert.False(h.Exists("app_offline.htm"));
        Assert.Equal("DATA", h.Read("uploads/u.png"));
        Assert.NotNull(h.Journal.LoadCompleted(id));
    }

    [Fact]
    public void Recovery_runs_once_a_second_start_finds_nothing_left_to_do()
    {
        Crash();
        Assert.Single(h.Executor.RecoverUnfinished(new CollectingReporter(), default));
        Assert.Empty(h.Executor.RecoverUnfinished(new CollectingReporter(), default));
    }

    [Fact]
    public void A_run_that_died_before_any_change_just_cleans_up_and_reports_failed()
    {
        h.Live("index.html", "v1");
        h.Live("app_offline.htm", "ours");
        var id = Guid.NewGuid();
        h.Journal.Save(new JournalEntry { RunId = id, Phase = "Started", TargetPath = h.Target, WeCreatedOffline = false, StartedAt = h.Clock.GetUtcNow() });
        var c = Assert.Single(h.Executor.RecoverUnfinished(new CollectingReporter(), default));
        Assert.Equal(RunResults.Failed, c.Result);
        Assert.Equal("v1", h.Read("index.html"));
        Assert.True(h.Exists("app_offline.htm"));          // not ours, left alone
    }

    [Fact]
    public void A_run_that_finished_but_whose_result_was_never_sent_is_reported_as_succeeded()
    {
        h.Live("index.html", "v2");
        var id = Guid.NewGuid();
        h.Journal.Save(new JournalEntry { RunId = id, Phase = "Done", TargetPath = h.Target, StartedAt = h.Clock.GetUtcNow() });
        Assert.Equal(RunResults.Succeeded, Assert.Single(h.Executor.RecoverUnfinished(new CollectingReporter(), default)).Result);
        Assert.Equal("v2", h.Read("index.html"));
    }

    [Fact]
    public void A_missing_backup_during_recovery_is_reported_as_rollback_failed_not_hidden()
    {
        var id = Crash();
        File.Delete(h.Backups.PathOf(h.Journal.Load(id)!.BackupId!));
        Assert.Equal(RunResults.RollbackFailed, Assert.Single(h.Executor.RecoverUnfinished(new CollectingReporter(), default)).Result);
    }

    [Fact]
    public async Task Cancelling_a_run_leaves_the_journal_so_the_next_start_can_restore()
    {
        h.Live("index.html", "v1");
        var cts = new CancellationTokenSource();
        h.Packages.Before = () => { cts.Cancel(); return Task.CompletedTask; };
        var cmd = h.Command(DeployHarness.Zip(("index.html", "v2")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Executor.DeployAsync(cmd, new CollectingReporter(), cts.Token));
        Assert.NotNull(h.Journal.Load(cmd.RunId));
        Assert.Null(h.Journal.LoadCompleted(cmd.RunId));
        Assert.Equal("v1", h.Read("index.html"));
    }
}

public class FolderInspectorTests : IDisposable
{
    private readonly DeployHarness h = new();
    public void Dispose() => h.Dispose();

    private ManifestRequest Req(bool listAll = false, string[]? data = null) => new(h.Target, 1, "/", listAll, data ?? ["uploads"]);
    private ManifestResult Get(string[] paths, bool listAll = false, string[]? data = null, ManifestRequest? req = null) => h.Inspector.GetManifest(Guid.NewGuid(), req ?? Req(listAll, data), paths);

    [Fact]
    public void Manifest_returns_hashes_for_requested_files_that_exist_and_skips_data_folders()
    {
        h.Live("index.html", "hello"); h.Live("uploads/u.png", "DATA");
        var r = Get(["index.html", "missing.txt", "uploads/u.png", "../../etc/passwd"]);
        Assert.True(r.Ok);
        var e = Assert.Single(r.Existing);
        Assert.Equal(("index.html", 5L, DeployHarness.Sha("hello")), (e.Path, e.Size, e.Sha256));
    }

    [Fact]
    public void List_all_lists_every_file_outside_data_folders_with_sizes_only()
    {
        h.Live("a.txt", "1"); h.Live("bin/b.dll", "22"); h.Live("uploads/u.png", "DATA"); h.Live("uploads/deep/x", "x");
        var r = Get([], listAll: true);
        Assert.Equal(["a.txt", "bin/b.dll"], r.AllFiles.Select(f => f.Path).OrderBy(x => x));
        Assert.All(r.AllFiles, f => Assert.Null(f.Sha256));
        Assert.Equal(2, r.TotalFiles);
    }

    [Fact]
    public void The_portal_cannot_make_the_agent_list_or_hash_a_data_folder_even_by_leaving_it_off_its_list()
    {
        h.Live("uploads/secret.dat", "DATA");
        Get([], data: ["uploads"]);                  // the agent learns "uploads"
        var r = Get(["uploads/secret.dat"], listAll: true, data: []);   // later request hides it
        Assert.Empty(r.Existing);
        Assert.Empty(r.AllFiles);
    }

    [Fact]
    public void Manifest_for_a_folder_outside_the_allowed_roots_is_refused()
    {
        var other = Path.Combine(h.Root, "elsewhere"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "x.txt"), "x");
        h.Sites.Path = other;
        var r = Get(["x.txt"], req: Req() with { PhysicalPath = other });
        Assert.False(r.Ok);
        Assert.Contains("outside the folders", r.Error);
    }

    [Fact]
    public void Masked_file_hides_secrets_on_the_agent_and_marks_changes_with_the_salt()
    {
        h.Live("web.config", "<configuration><connectionStrings><add name=\"Db\" connectionString=\"Server=s;Password=Hunter2;\"/></connectionStrings></configuration>");
        var r = h.Inspector.GetMaskedFile(Guid.NewGuid(), new MaskedFileRequest(h.Target, 1, "/", "web.config", "salt1", ["web.config"]));
        Assert.True(r.Ok); Assert.True(r.Exists);
        Assert.DoesNotContain("Hunter2", r.MaskedText);
        Assert.Contains("Server=s", r.MaskedText);
        var again = h.Inspector.GetMaskedFile(Guid.NewGuid(), new MaskedFileRequest(h.Target, 1, "/", "web.config", "salt2", ["web.config"]));
        Assert.NotEqual(r.MaskedText, again.MaskedText);
    }

    [Fact]
    public void Masked_file_reports_a_missing_file_without_error()
    {
        var r = h.Inspector.GetMaskedFile(Guid.NewGuid(), new MaskedFileRequest(h.Target, 1, "/", "appsettings.json", "s", ["appsettings*.json"]));
        Assert.True(r.Ok); Assert.False(r.Exists);
    }

    [Theory]
    [InlineData("index.html")]                    // not a protected config file
    [InlineData("bin/secret.config")]              // not matched by the root-only pattern
    [InlineData("../outside.json")]               // traversal
    [InlineData("uploads/data.json")]             // data folder
    [InlineData("app.exe")]                       // not text-like
    public void Masked_file_is_only_for_protected_config_files(string path)
    {
        h.Live("index.html", "x"); h.Live("bin/secret.config", "x"); h.Live("uploads/data.json", "x");
        var r = h.Inspector.GetMaskedFile(Guid.NewGuid(), new MaskedFileRequest(h.Target, 1, "/", path, "s", ["web.config", "appsettings*.json"]));
        Assert.False(r.Ok);
        Assert.Null(r.MaskedText);
    }

    [Fact]
    public void Large_config_files_are_cut_and_flagged()
    {
        h.Live("appsettings.json", new string('a', 1_500_000));
        var r = h.Inspector.GetMaskedFile(Guid.NewGuid(), new MaskedFileRequest(h.Target, 1, "/", "appsettings.json", "s", ["appsettings*.json"]));
        Assert.True(r.Ok); Assert.True(r.Truncated);
    }
}
