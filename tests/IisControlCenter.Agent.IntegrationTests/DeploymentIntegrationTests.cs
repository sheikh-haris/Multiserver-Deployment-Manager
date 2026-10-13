using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using IisControlCenter.Agent.Deploy;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Web.Administration;
using Xunit;

namespace IisControlCenter.Agent.IntegrationTests;

/// <summary>A real IIS site that serves static files, plus the real agent classes (IIS lookup, pool recycle, HTTP health check).</summary>
[SupportedOSPlatform("windows")]
public sealed class DeployIisFixture : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Root { get; }
    public string Web { get; }
    public string Host => $"{Id}.icc-it.local";
    public int Port { get; }
    public long SiteId { get; }
    public string PoolName => $"icc-it-deploy-{Id}";
    public bool Available { get; }
    public string SkipReason { get; } = "";
    public AgentDirs Dirs { get; }
    private readonly string _siteName;

    public DeployIisFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "icc-itd-" + Id);
        Web = Path.Combine(Root, "sites", "web");
        Port = 0; SiteId = 0; Dirs = null!; _siteName = "";
        if (!OperatingSystem.IsWindows()) { SkipReason = "Not Windows."; return; }
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (!new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) { SkipReason = "Needs administrator rights."; return; }
        if (!new IisInspector().IsIisInstalled()) { SkipReason = "IIS is not installed."; return; }

        Directory.CreateDirectory(Web);
        // A folder under the user's temp directory is not readable by IIS (anonymous requests run as IUSR). Without this every request is a 403.
        // Files the agent writes later inherit this permission.
        var icacls = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", $"\"{Root}\" /grant \"IIS_IUSRS:(OI)(CI)RX\" \"IUSR:(OI)(CI)RX\" /Q") { UseShellExecute = false, CreateNoWindow = true })!;
        icacls.WaitForExit();
        if (icacls.ExitCode != 0) throw new InvalidOperationException("icacls failed with " + icacls.ExitCode);
        Dirs = new AgentDirs(Path.Combine(Root, "agent"));
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _siteName = $"icc-it-deploy-site-{Id}";
        using var mgr = new ServerManager();
        mgr.ApplicationPools.Add(PoolName).ManagedRuntimeVersion = "";
        var site = mgr.Sites.Add(_siteName, "http", $"*:{Port}:{Host}", Web);
        site.Applications["/"].ApplicationPoolName = PoolName;
        // Static files are served by IIS directly. Give the pool identity read access to the folder.
        mgr.CommitChanges();
        SiteId = mgr.Sites[_siteName].Id;
        Available = true;
    }

    public async Task<(HttpStatusCode Code, string Body)> Get(string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}{path}");
        req.Headers.Host = Host;
        using var r = await http.SendAsync(req);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        if (!Available) return;
        try { using var m = new ServerManager(); m.Sites[_siteName]?.Delete(); m.ApplicationPools[PoolName]?.Delete(); m.CommitChanges(); } catch { }
        try { Directory.Delete(Root, true); } catch { }
    }
}

[SupportedOSPlatform("windows")]
public class DeploymentIntegrationTests : IClassFixture<DeployIisFixture>
{
    private readonly DeployIisFixture fx;
    public DeploymentIntegrationTests(DeployIisFixture fx) => this.fx = fx;

    private sealed class LocalPackage(byte[] bytes) : IPackageSource
    {
        public Task DownloadAsync(Guid runId, Guid packageId, string dest, CancellationToken ct) { File.WriteAllBytes(dest, bytes); return Task.CompletedTask; }
    }
    private sealed class NoAv : IAgentAntivirus { public Task<ScanResult> ScanAsync(string f, CancellationToken ct) => Task.FromResult(new ScanResult(ScanOutcome.Clean, "n/a")); }
    private sealed class Reporter : IRunReporter
    {
        public List<string> Lines { get; } = [];
        public void Step(Guid runId, string step, string status, string? detail = null) => Lines.Add($"[{step}] {status}");
        public void Log(Guid runId, string level, string step, string message) => Lines.Add($"{level} {step}: {message}");
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, c) in files) { using var s = z.CreateEntry(n).Open(); s.Write(Encoding.UTF8.GetBytes(c)); }
        return ms.ToArray();
    }

    private sealed class Plans : IRunPlanSource
    {
        public Dictionary<Guid, RunPlanInput> Map { get; } = [];
        public Task<RunPlanInput> GetAsync(Guid runId, CancellationToken ct) => Task.FromResult(Map[runId]);
    }
    private readonly Plans plans = new();

    private DeploymentExecutor Executor(byte[] package, int copyRetries = 3)
    {
        var allowed = Path.Combine(fx.Root, "sites");
        var options = new ExecutorOptions
        {
            IsPathAllowed = p => Path.GetFullPath(p).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            CopyRetries = copyRetries, CopyRetryDelay = TimeSpan.FromMilliseconds(200),
        };
        return new DeploymentExecutor(fx.Dirs, options, new LocalPackage(package), new LocalHealthChecker(), new IisSiteResolver(), new IisPoolController(), new NoAv(), plans,
            new ProtectedStore(fx.Dirs), new BackupStore(fx.Dirs), new RunJournal(fx.Dirs), TimeProvider.System, NullLogger<DeploymentExecutor>.Instance);
    }

    private DeployCommand Command(byte[] zip, DeployMode mode = DeployMode.Overlay, string healthPath = "/health.txt")
    {
        var pp = new ProtectedPaths(["web.config"], ["uploads"]);
        var files = new List<PackageFile>();
        using (var z = new ZipArchive(new MemoryStream(zip)))
            foreach (var e in z.Entries) { using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); files.Add(new PackageFile(e.FullName, ms.Length, Convert.ToHexString(SHA256.HashData(ms.ToArray())))); }
        var snapshot = files.Select(f => { var l = FolderScanner.Probe(fx.Web, f.Path, true); return new SnapshotFile(f.Path, l?.Size ?? 0, l?.Sha256); }).ToList();
        var deletes = new List<string>();
        if (mode == DeployMode.FullReplace)
        {
            var live = snapshot.Where(s => s.Sha256 is not null).ToDictionary(s => s.Path, s => new LiveFile(s.Path, s.Size, s.Sha256), StringComparer.OrdinalIgnoreCase);
            deletes = DeployPlanner.Plan(files, live, FolderScanner.All(fx.Web, pp, false), mode, pp).Deletes.Select(d => d.Path).ToList();
        }
        var runId = Guid.NewGuid();
        plans.Map[runId] = new RunPlanInput(snapshot, deletes);
        return new DeployCommand(runId, Guid.NewGuid(), Guid.NewGuid(), fx.Web, fx.SiteId, "/", Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(zip)), zip.Length, mode,
            pp.ConfigPatterns, pp.DataFolders, [], 5, healthPath, 200, 20, [], false);
    }

    private void Reset(params (string Name, string Content)[] files)
    {
        foreach (var f in Directory.GetFiles(fx.Web, "*", SearchOption.AllDirectories)) File.Delete(f);
        foreach (var (n, c) in files) { var p = Path.Combine(fx.Web, n.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    }

    [SkippableFact]
    public async Task Deploys_to_a_real_iis_site_and_the_new_content_is_served()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Reset(("health.txt", "ok v1"), ("index.html", "version 1"), ("uploads/u.txt", "USER DATA"));
        Assert.Equal("version 1", (await fx.Get("/index.html")).Body);

        var zip = Zip(("health.txt", "ok v2"), ("index.html", "version 2"), ("new.html", "brand new"));
        var rep = new Reporter();
        var r = await Executor(zip).DeployAsync(Command(zip), rep, default);

        Assert.True(r.Result == RunResults.Succeeded, r.Message + "\n" + string.Join("\n", rep.Lines));
        Assert.Equal("version 2", (await fx.Get("/index.html")).Body);
        Assert.Equal("brand new", (await fx.Get("/new.html")).Body);
        Assert.Equal("USER DATA", File.ReadAllText(Path.Combine(fx.Web, "uploads", "u.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Web, "app_offline.htm")));
        Assert.Contains(rep.Lines, l => l.Contains("Recycled app pool"));
        Assert.Contains(rep.Lines, l => l.Contains("GET http://127.0.0.1"));
    }

    [SkippableFact]
    public async Task A_failing_health_check_rolls_back_and_the_old_site_is_served_again()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Reset(("health.txt", "ok v1"), ("index.html", "version 1"));
        var zip = Zip(("index.html", "BROKEN version 3"));                       // the package has no health.txt in it... it exists from v1 (200), so make health point elsewhere
        var cmd = Command(zip, healthPath: "/does-not-exist.txt");               // 404 -> health fails
        var rep = new Reporter();
        var r = await Executor(zip).DeployAsync(cmd with { HealthTimeoutSeconds = 6 }, rep, default);

        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal("Health", r.FailureStep);
        Assert.Equal("version 1", (await fx.Get("/index.html")).Body);
        Assert.False(File.Exists(Path.Combine(fx.Web, "app_offline.htm")));
    }

    [SkippableFact]
    public async Task A_file_locked_by_another_process_makes_the_deployment_fail_cleanly_and_roll_back_without_corrupting_anything()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Reset(("health.txt", "ok"), ("locked.dll", "LOCKED v1"), ("index.html", "version 1"));
        var zip = Zip(("index.html", "version 2"), ("locked.dll", "LOCKED v2"));
        var cmd = Command(zip);
        using var hold = new FileStream(Path.Combine(fx.Web, "locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);   // like a DLL loaded by a running app: others may read it (backup) but not replace it
        var r = await Executor(zip, copyRetries: 2).DeployAsync(cmd, new Reporter(), default);

        Assert.Equal(RunResults.RolledBack, r.Result);
        Assert.Equal("Copy", r.FailureStep);
        hold.Dispose();
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(fx.Web, "index.html")));
        Assert.Equal("LOCKED v1", File.ReadAllText(Path.Combine(fx.Web, "locked.dll")));
        Assert.Equal("version 1", (await fx.Get("/index.html")).Body);
    }

    [SkippableFact]
    public async Task The_agent_resolves_the_real_iis_folder_and_refuses_a_different_one()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Reset(("health.txt", "ok"), ("index.html", "version 1"));
        var other = Path.Combine(fx.Root, "sites", "other"); Directory.CreateDirectory(other);
        var zip = Zip(("index.html", "x"));
        var r = await Executor(zip).DeployAsync(Command(zip) with { PhysicalPath = other }, new Reporter(), default);
        Assert.Equal(RunResults.NeedsReapproval, r.Result);
        Assert.Empty(Directory.GetFileSystemEntries(other));
    }

    [SkippableFact]
    public async Task Manual_rollback_restores_the_previous_version_on_real_iis()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Reset(("health.txt", "ok"), ("index.html", "version 1"));
        var zip = Zip(("index.html", "version 2"));
        var ex = Executor(zip);
        var d = await ex.DeployAsync(Command(zip), new Reporter(), default);
        Assert.Equal(RunResults.Succeeded, d.Result);
        var r = await ex.RollbackAsync(new RollbackCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fx.Web, fx.SiteId, "/", d.BackupId!, "/health.txt", 200, 20), new Reporter(), default);
        Assert.Equal(RunResults.Succeeded, r.Result);
        Assert.Equal("version 1", (await fx.Get("/index.html")).Body);
    }

    [SkippableFact]
    public void Pool_controller_recycles_a_started_pool_and_reports_unknown_pools()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        Assert.Contains("pool", new IisPoolController().Restart(fx.PoolName), StringComparison.OrdinalIgnoreCase);
        Assert.ThrowsAny<Exception>(() => new IisPoolController().Restart("does-not-exist-" + fx.Id));
    }

    [SkippableFact]
    public void Site_resolver_returns_folder_pool_and_bindings_from_real_iis()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var app = new IisSiteResolver().Resolve(fx.SiteId, "/");
        Assert.NotNull(app);
        Assert.Equal(fx.Web, app!.PhysicalPath, ignoreCase: true);
        Assert.Equal(fx.PoolName, app.PoolName);
        Assert.Contains(app.Bindings, b => b.Port == fx.Port && b.Host == fx.Host);
        Assert.Null(new IisSiteResolver().Resolve(999999, "/"));
        Assert.Null(new IisSiteResolver().Resolve(fx.SiteId, "/nope"));
    }
}
