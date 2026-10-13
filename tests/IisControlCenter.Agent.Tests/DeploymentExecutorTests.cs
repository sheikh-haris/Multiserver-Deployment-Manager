using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Agent.Deploy;

namespace IisControlCenter.Agent.Tests;

public class DeploymentExecutorTests : IDisposable
{
    private readonly DeployHarness h = new();
    public void Dispose() => h.Dispose();

    private void BasicSite()
    {
        h.Live("index.html", "old index");
        h.Live("bin/App.dll", "dll v1");
        h.Live("web.config", "<configuration><!-- prod settings --></configuration>");
        h.Live("appsettings.json", "{\"Env\":\"prod\"}");
        h.Live("uploads/user1.png", "USER DATA 1");
        h.Live("uploads/deep/user2.png", "USER DATA 2");
        h.Live("old-only.txt", "left over");
    }

    private static byte[] NewVersion() => DeployHarness.Zip(
        ("index.html", "new index"), ("bin/App.dll", "dll v2"), ("bin/New.dll", "new dll"),
        ("web.config", "<configuration><!-- DEV settings --></configuration>"), ("appsettings.json", "{\"Env\":\"dev\"}"),
        ("uploads/readme.txt", "should never be written"));

    // ---------------- the happy path ----------------

    [Fact]
    public async Task Overlay_adds_and_replaces_files_but_keeps_protected_config_and_data_and_deletes_nothing()
    {
        BasicSite();
        var rep = new CollectingReporter();
        var r = await h.Deploy(h.Command(NewVersion()), rep);

        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("new index", h.Read("index.html"));
        Assert.Equal("dll v2", h.Read("bin/App.dll"));
        Assert.Equal("new dll", h.Read("bin/New.dll"));
        Assert.Contains("prod settings", h.Read("web.config"));                  // protected config exists: skipped
        Assert.Equal("{\"Env\":\"prod\"}", h.Read("appsettings.json"));
        Assert.Equal("USER DATA 1", h.Read("uploads/user1.png"));                // data untouched
        Assert.Equal("USER DATA 2", h.Read("uploads/deep/user2.png"));
        Assert.False(h.Exists("uploads/readme.txt"));                            // never written, even though it was in the zip
        Assert.Equal("left over", h.Read("old-only.txt"));                       // overlay never deletes
        Assert.False(h.Exists("app_offline.htm"));
    }

    [Fact]
    public async Task Counts_are_reported_and_every_step_runs_in_order()
    {
        BasicSite();
        var rep = new CollectingReporter();
        var r = await h.Deploy(h.Command(NewVersion()), rep);
        Assert.Equal(1, r.Added);          // bin/New.dll
        Assert.Equal(2, r.Replaced);       // index.html, bin/App.dll
        Assert.Equal(0, r.Deleted);
        Assert.Equal(3, r.Skipped);        // web.config, appsettings.json, uploads/readme.txt
        var order = rep.Steps.Where(s => s.Status == "Started").Select(s => s.Step).ToList();
        Assert.Equal([Steps.Validate, Steps.Download, Steps.Extract, Steps.Snapshot, Steps.Backup, Steps.Offline, Steps.Copy, Steps.Restart, Steps.Online, Steps.Health], order);
        Assert.Equal(["AppPool1"], h.Pools.Restarted);
        Assert.NotNull(r.BackupId);
        Assert.True(File.Exists(r.BackupPath));
    }

    [Fact]
    public async Task Backup_contains_the_site_but_never_the_data_folders()
    {
        BasicSite();
        var r = await h.Deploy(h.Command(NewVersion()));
        using var zip = System.IO.Compression.ZipFile.OpenRead(r.BackupPath!);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("index.html", names); Assert.Contains("bin/App.dll", names); Assert.Contains("web.config", names); Assert.Contains("old-only.txt", names);
        Assert.DoesNotContain(names, n => n.StartsWith("uploads"));
        using var rd = new StreamReader(zip.GetEntry("index.html")!.Open());
        Assert.Equal("old index", rd.ReadToEnd());                              // the OLD content
    }

    [Fact]
    public async Task First_time_deployment_copies_config_files_that_do_not_exist_yet()
    {
        var r = await h.Deploy(h.Command(DeployHarness.Zip(("web.config", "<new/>"), ("appsettings.json", "{}"), ("index.html", "hi"))));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("<new/>", h.Read("web.config"));
        Assert.Equal(3, r.Added);
    }

    [Fact]
    public async Task Force_overwrite_replaces_a_protected_config_file_as_approved()
    {
        BasicSite();
        var rep = new CollectingReporter();
        var r = await h.Deploy(h.Command(NewVersion(), forced: ["web.config"]), rep);
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Contains("DEV settings", h.Read("web.config"));
        Assert.Contains("{\"Env\":\"prod\"}", h.Read("appsettings.json"));      // only the forced one
        Assert.Contains(rep.Logs, l => l.Level == "Warning" && l.Message.Contains("Force-overwrote"));
    }

    // ---------------- Full Replace ----------------

    [Fact]
    public async Task Full_replace_makes_the_folder_match_the_package_but_keeps_data_and_config()
    {
        BasicSite();
        var r = await h.Deploy(h.Command(NewVersion(), DeployMode.FullReplace));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.False(h.Exists("old-only.txt"));                                  // deleted
        Assert.Equal(1, r.Deleted);
        Assert.Equal("USER DATA 1", h.Read("uploads/user1.png"));
        Assert.Contains("prod settings", h.Read("web.config"));
        var live = h.Snapshot().Keys.OrderBy(x => x).ToList();
        Assert.Equal(["appsettings.json", "bin/App.dll", "bin/New.dll", "index.html", "uploads/deep/user2.png", "uploads/user1.png", "web.config"], live);
    }

    [Fact]
    public async Task Full_replace_removes_folders_that_became_empty_but_never_a_data_folder()
    {
        BasicSite();
        h.Live("legacy/old/a.txt", "x");
        var r = await h.Deploy(h.Command(NewVersion(), DeployMode.FullReplace));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.False(Directory.Exists(Path.Combine(h.Target, "legacy")));
        Assert.True(Directory.Exists(Path.Combine(h.Target, "uploads", "deep")));
    }

    [Fact]
    public async Task An_application_in_a_sub_folder_is_left_alone_by_the_parents_deployment_even_with_full_replace_and_the_portal_saying_nothing()
    {
        BasicSite();
        h.Live("api/bin/Api.dll", "CHILD APP v1"); h.Live("api/web.config", "child config");
        h.Sites.OtherFolders.Add(Path.Combine(h.Target, "api"));
        // The package even contains a file under api/. It must not be written. The portal's list does not mention api/ at all.
        var zip = DeployHarness.Zip(("index.html", "v2"), ("api/bin/Api.dll", "WRONG PACKAGE"), ("bin/New.dll", "n"));
        var r = await h.Deploy(h.Command(zip, DeployMode.FullReplace, dataFolders: ["uploads"]));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("CHILD APP v1", h.Read("api/bin/Api.dll"));
        Assert.Equal("child config", h.Read("api/web.config"));
        Assert.Equal("v2", h.Read("index.html"));
        Assert.False(h.Exists("old-only.txt"));
        // and it is not in the backup, so a rollback can never touch it either
        var rb = await h.Executor.RollbackAsync(new RollbackCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), h.Target, 1, "/", r.BackupId!, "/", 0, 5), new CollectingReporter(), default);
        Assert.Equal(RunResults.Succeeded, rb.Result);
        Assert.Equal("CHILD APP v1", h.Read("api/bin/Api.dll"));
    }

    [Fact]
    public async Task The_agent_remembers_protected_data_folders_so_the_portal_cannot_remove_them()
    {
        BasicSite();
        await h.Deploy(h.Command(NewVersion(), dataFolders: ["uploads"]));
        // A later request (mistaken or hostile) sends an EMPTY data folder list and Full Replace.
        var r = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v3")), DeployMode.FullReplace, dataFolders: []));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("USER DATA 1", h.Read("uploads/user1.png"));
        Assert.Equal("USER DATA 2", h.Read("uploads/deep/user2.png"));
    }

    // ---------------- checks before anything changes ----------------

    [Fact]
    public async Task Wrong_package_hash_stops_before_any_change_and_makes_no_backup()
    {
        BasicSite();
        var before = h.Snapshot();
        var cmd = h.Command(NewVersion(), packageSha: new string('A', 64));
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.Failed, r.Result);
        Assert.Equal(Steps.Download, r.FailureStep);
        Assert.Contains("hash does not match", r.Message);
        Assert.Equal(before, h.Snapshot());
        Assert.Null(r.BackupId);
        Assert.Empty(h.Backups.ForTarget(h.Target));
    }

    [Fact]
    public async Task Wrong_package_size_is_refused()
    {
        BasicSite();
        var r = await h.Deploy(h.Command(NewVersion(), packageSize: 5));
        Assert.Equal(Steps.Download, r.FailureStep);
    }

    [Fact]
    public async Task A_folder_outside_the_local_allow_list_is_refused_whatever_the_portal_says()
    {
        BasicSite();
        var other = Path.Combine(h.Root, "elsewhere"); Directory.CreateDirectory(other);
        h.Sites.Path = other;
        var cmd = h.Command(NewVersion()) with { PhysicalPath = other };
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.Failed, r.Result);
        Assert.Equal(Steps.Validate, r.FailureStep);
        Assert.Contains("outside the folders", r.Message);
        Assert.Empty(Directory.GetFileSystemEntries(other));
    }

    [Fact]
    public async Task The_agent_refuses_a_folder_that_is_not_the_one_IIS_uses_for_that_site()
    {
        BasicSite();
        var sibling = Path.Combine(h.Root, "sites", "billing", "web"); Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "billing data");
        var cmd = h.Command(NewVersion()) with { PhysicalPath = sibling };      // inside the allowed root, but another application's folder
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.NeedsReapproval, r.Result);
        Assert.Contains("not the folder IIS uses", r.Message);
        Assert.Equal("billing data", File.ReadAllText(Path.Combine(sibling, "keep.txt")));
        Assert.Single(Directory.GetFileSystemEntries(sibling));
    }

    [Fact]
    public async Task Unknown_site_is_refused()
    {
        BasicSite();
        h.Sites.Exists = false;
        var r = await h.Deploy(h.Command(NewVersion()));
        Assert.Equal(Steps.Validate, r.FailureStep);
        Assert.Contains("was not found", r.Message);
    }

    // ---------------- zip content checks on the agent (it does not trust the portal's validation) ----------------

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("C:/evil.dll")]
    [InlineData("/evil.dll")]
    public async Task Zip_slip_entries_are_refused_and_nothing_is_written_anywhere(string name)
    {
        BasicSite();
        var before = h.Snapshot();
        var r = await h.Deploy(h.Command(DeployHarness.Zip(("ok.txt", "x"), (name, "x")), takeSnapshot: false));
        Assert.Equal(RunResults.Failed, r.Result);
        Assert.Equal(Steps.Extract, r.FailureStep);
        Assert.Equal(before, h.Snapshot());
        Assert.False(File.Exists(Path.Combine(h.Root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(h.Root, "sites", "evil.dll")));
    }

    [Fact]
    public async Task Blocked_file_types_are_refused_unless_the_component_allows_them()
    {
        BasicSite();
        var zip = DeployHarness.Zip(("tool.exe", "MZ"), ("index.html", "x"));
        var blocked = await h.Deploy(h.Command(zip));
        Assert.Equal(Steps.Extract, blocked.FailureStep);
        var allowed = await h.Deploy(h.Command(zip, extraExt: [".exe"]));
        Assert.Equal(RunResults.Succeeded, allowed.Result);
    }

    [Fact]
    public async Task A_package_cannot_contain_app_offline_htm()
    {
        var r = await h.Deploy(h.Command(DeployHarness.Zip(("app_offline.htm", "x"), ("a.txt", "x")), takeSnapshot: false));
        Assert.Equal(Steps.Extract, r.FailureStep);
    }

    [Fact]
    public async Task Antivirus_on_the_agent_blocks_infected_or_unscannable_packages()
    {
        BasicSite();
        h.Av.Outcome = IisControlCenter.Contracts.Deployments.ScanOutcome.Infected;
        var bad = await h.Deploy(h.Command(NewVersion(), scanOnAgent: true));
        Assert.Equal(Steps.Scan, bad.FailureStep); Assert.Contains("threat", bad.Message);
        h.Av.Outcome = IisControlCenter.Contracts.Deployments.ScanOutcome.Unavailable;
        var cannot = await h.Deploy(h.Command(NewVersion(), scanOnAgent: true));
        Assert.Equal(Steps.Scan, cannot.FailureStep); Assert.Contains("could not be scanned", cannot.Message);
        Assert.Equal("old index", h.Read("index.html"));
        h.Av.Outcome = IisControlCenter.Contracts.Deployments.ScanOutcome.Clean;
        Assert.Equal(RunResults.Succeeded, (await h.Deploy(h.Command(NewVersion(), scanOnAgent: true))).Result);
    }

    // ---------------- "the folder changed since the approver looked" ----------------

    [Fact]
    public async Task A_changed_live_file_stops_the_run_as_needs_reapproval_with_no_changes()
    {
        BasicSite();
        var cmd = h.Command(NewVersion());
        h.Live("index.html", "someone edited this on the server after approval");
        var before = h.Snapshot();
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.NeedsReapproval, r.Result);
        Assert.Equal(Steps.Snapshot, r.FailureStep);
        Assert.Contains("index.html", r.Message);
        Assert.Equal(before, h.Snapshot());
        Assert.Empty(h.Backups.ForTarget(h.Target));
    }

    [Fact]
    public async Task A_file_that_appeared_or_vanished_since_approval_also_needs_reapproval()
    {
        BasicSite();
        var cmd = h.Command(NewVersion());
        File.Delete(Path.Combine(h.Target, "bin", "App.dll"));
        Assert.Equal(RunResults.NeedsReapproval, (await h.Deploy(cmd)).Result);
        var cmd2 = h.Command(NewVersion());
        h.Live("bin/New.dll", "appeared");
        Assert.Equal(RunResults.NeedsReapproval, (await h.Deploy(cmd2)).Result);
    }

    [Fact]
    public async Task Changes_to_files_the_deployment_does_not_touch_do_not_matter_logs_and_so_on()
    {
        BasicSite();
        var cmd = h.Command(NewVersion());
        h.Live("logs/today.log", "the app writes logs all the time");
        h.Live("unrelated.txt", "x");
        Assert.Equal(RunResults.Succeeded, (await h.Deploy(cmd)).Result);
    }

    [Fact]
    public async Task Full_replace_notices_a_new_file_that_would_now_be_deleted()
    {
        BasicSite();
        var cmd = h.Command(NewVersion(), DeployMode.FullReplace);
        h.Live("brand-new-user-file.txt", "added by the app after approval");
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.NeedsReapproval, r.Result);
        Assert.Contains("would delete", r.Message);
        Assert.True(h.Exists("brand-new-user-file.txt"));
    }

    [Fact]
    public async Task Edits_inside_protected_data_folders_never_matter()
    {
        BasicSite();
        var cmd = h.Command(NewVersion(), DeployMode.FullReplace);
        h.Live("uploads/new-upload.png", "a user just uploaded this");
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("a user just uploaded this", h.Read("uploads/new-upload.png"));
    }

    // ---------------- failure and automatic rollback ----------------

    [Fact]
    public async Task Failed_health_check_restores_the_previous_version_exactly()
    {
        BasicSite();
        var before = h.Snapshot();
        h.Health.Results.Enqueue(false);        // after the deployment
        h.Health.Results.Enqueue(true);         // after the restore
        var rep = new CollectingReporter();
        var r = await h.Deploy(h.Command(NewVersion(), DeployMode.FullReplace), rep);

        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal(Steps.Health, r.FailureStep);
        Assert.Contains("restored automatically", r.Message);
        Assert.Equal(before, h.Snapshot());                                     // byte-for-byte the old state, deleted files back, new files gone
        Assert.False(h.Exists("app_offline.htm"));
        Assert.Equal(2, h.Pools.Restarted.Count);                               // once for the deploy, once for the restore
        Assert.True(rep.Reached(Steps.Rollback));
    }

    [Fact]
    public async Task Rollback_after_a_failure_never_touches_user_data()
    {
        BasicSite();
        h.Health.Results.Enqueue(false);
        var cmd = h.Command(NewVersion(), DeployMode.FullReplace);
        h.Live("uploads/upload-during-deploy.png", "uploaded while the deployment ran");   // not part of the plan, protected
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal("uploaded while the deployment ran", h.Read("uploads/upload-during-deploy.png"));
        Assert.Equal("USER DATA 1", h.Read("uploads/user1.png"));
    }

    [Fact]
    public async Task A_copy_failure_halfway_is_rolled_back()
    {
        BasicSite();
        var before = h.Snapshot();
        // A directory where the package wants a file makes the move fail after earlier files were already written.
        Directory.CreateDirectory(Path.Combine(h.Target, "bin", "New.dll"));
        File.WriteAllText(Path.Combine(h.Target, "bin", "New.dll", "x"), "x");
        var cmd = h.Command(DeployHarness.Zip(("index.html", "new index"), ("bin/New.dll", "new dll"), ("zzz.txt", "z")));
        var r = await h.Deploy(cmd);
        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal(Steps.Copy, r.FailureStep);
        Assert.Equal("old index", h.Read("index.html"));
        Assert.False(h.Exists("zzz.txt"));
        Assert.False(h.Exists("app_offline.htm"));
        Assert.Contains("old index", string.Join("", before.Where(kv => kv.Key == "index.html").Select(kv => kv.Value)));
    }

    [Fact]
    public async Task A_failing_pool_restart_is_rolled_back()
    {
        BasicSite();
        h.Pools.ShouldFail = n => n == 1;
        var r = await h.Deploy(h.Command(NewVersion()));
        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal(Steps.Restart, r.FailureStep);
        Assert.Equal("old index", h.Read("index.html"));
    }

    [Fact]
    public async Task If_the_restore_itself_fails_the_result_says_so_loudly()
    {
        BasicSite();
        h.Health.Results.Enqueue(false);
        h.Pools.ShouldFail = n => n >= 2;       // deploy restart works, restore restart fails
        var r = await h.Deploy(h.Command(NewVersion()));
        Assert.Equal(RunResults.RollbackFailed, r.Result);
        Assert.Contains("needs a person", r.Message?.ToLowerInvariant());
        Assert.Contains("RESTORE FAILED", r.Message);
    }

    [Fact]
    public async Task Not_enough_disk_space_stops_before_any_change()
    {
        using var small = new DeployHarness(freeSpace: () => 10);
        small.Live("index.html", new string('x', 1000));
        var cmd = small.Command(DeployHarness.Zip(("index.html", "new")));
        var r = await small.Deploy(cmd);
        Assert.Equal(RunResults.Failed, r.Result);
        Assert.Equal(Steps.Backup, r.FailureStep);
        Assert.Contains("free disk space", r.Message);
        Assert.Equal(new string('x', 1000), small.Read("index.html"));
    }

    // ---------------- links ----------------

    [Fact]
    public async Task The_agent_never_writes_through_a_folder_link_to_somewhere_else()
    {
        BasicSite();
        var outside = Path.Combine(h.Root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside data");
        try { Directory.CreateSymbolicLink(Path.Combine(h.Target, "linked"), outside); }
        catch (Exception) { return; }      // the platform does not allow creating links here: nothing to test
        var r = await h.Deploy(h.Command(DeployHarness.Zip(("linked/pwned.txt", "x"), ("index.html", "new"))));
        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Contains("link", r.Message);
        Assert.False(File.Exists(Path.Combine(outside, "pwned.txt")));
        Assert.Equal("outside data", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        Assert.Equal("old index", h.Read("index.html"));                         // rolled back
    }

    // ---------------- offline marker ----------------

    [Fact]
    public async Task An_app_offline_htm_that_an_admin_put_there_is_left_in_place()
    {
        BasicSite();
        h.Live("app_offline.htm", "maintenance by admin");
        var r = await h.Deploy(h.Command(NewVersion()));
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("maintenance by admin", h.Read("app_offline.htm"));
    }

    // ---------------- retention ----------------

    [Fact]
    public async Task Only_the_newest_backups_are_kept_per_target()
    {
        BasicSite();
        for (var i = 0; i < 4; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(5));
            var r = await h.Deploy(h.Command(DeployHarness.Zip(("index.html", "v" + i)), keep: 2));
            Assert.Equal(RunResults.Succeeded, r.Result);
        }
        var left = h.Backups.ForTarget(h.Target);
        Assert.Equal(2, left.Count);
        Assert.All(left, b => Assert.True(File.Exists(h.Backups.PathOf(b.BackupId))));
        Assert.Equal(2, Directory.GetFiles(h.Dirs.Backups, "*.zip").Length);
    }

    [Fact]
    public async Task Staging_files_are_always_cleaned_up()
    {
        BasicSite();
        await h.Deploy(h.Command(NewVersion()));
        await h.Deploy(h.Command(NewVersion(), packageSha: new string('B', 64)));
        Assert.Empty(Directory.GetDirectories(h.Dirs.Staging));
    }

    // ---------------- journal and idempotency ----------------

    [Fact]
    public async Task The_result_is_kept_on_disk_until_the_portal_confirms_it()
    {
        BasicSite();
        var cmd = h.Command(NewVersion());
        var r = await h.Deploy(cmd);
        Assert.Equal(r.Result, h.Journal.LoadCompleted(cmd.RunId)!.Result);
        Assert.Single(h.Journal.AllCompleted());
        h.Journal.Acknowledge(cmd.RunId);
        Assert.Empty(h.Journal.AllCompleted());
        Assert.Null(h.Journal.Load(cmd.RunId));
    }
}
