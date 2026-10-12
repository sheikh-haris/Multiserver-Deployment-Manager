using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using IisControlCenter.Agent.Deploy;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IisControlCenter.Agent.Tests;

public sealed class FakeSites(string path, string pool = "AppPool1") : ISiteResolver
{
    public string Path { get; set; } = path;
    public bool Exists { get; set; } = true;
    public ResolvedApp? Resolve(long iisSiteId, string appPath) => Exists ? new ResolvedApp(Path, pool, [new DiscoveredBinding("http", "*", 8080, "site.local", null)]) : null;
}

public sealed class FakePools : IPoolController
{
    public List<string> Restarted { get; } = [];
    public Func<int, bool> ShouldFail { get; set; } = _ => false;
    public string Restart(string poolName)
    {
        Restarted.Add(poolName);
        if (ShouldFail(Restarted.Count)) throw new InvalidOperationException("pool refused to restart");
        return $"Recycled app pool '{poolName}'.";
    }
}

public sealed class FakeHealth : IHealthChecker
{
    public Queue<bool> Results { get; } = new();
    public bool Default { get; set; } = true;
    public int Calls { get; private set; }
    public Task<HealthResult> CheckAsync(IReadOnlyList<DiscoveredBinding> bindings, string path, int expectedStatus, TimeSpan timeout, CancellationToken ct)
    {
        Calls++;
        var ok = Results.Count > 0 ? Results.Dequeue() : Default;
        return Task.FromResult(new HealthResult(ok, ok ? "GET /health -> 200" : "GET /health -> 500"));
    }
}

public sealed class FakeAv : IAgentAntivirus
{
    public ScanOutcome Outcome { get; set; } = ScanOutcome.Clean;
    public Task<ScanResult> ScanAsync(string file, CancellationToken ct) => Task.FromResult(new ScanResult(Outcome, Outcome.ToString()));
}

public sealed class FakePlans : IRunPlanSource, IManifestInputSource
{
    public Dictionary<Guid, RunPlanInput> Plans { get; } = [];
    public List<string> ManifestPaths { get; set; } = [];
    public Task<RunPlanInput> GetAsync(Guid runId, CancellationToken ct) => Task.FromResult(Plans.TryGetValue(runId, out var p) ? p : new RunPlanInput([], []));
    public Task<IReadOnlyList<string>> GetPathsAsync(Guid commandId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(ManifestPaths);
}

public sealed class FakePackages : IPackageSource
{
    public byte[]? Content { get; set; }
    public Func<Task>? Before { get; set; }
    public async Task DownloadAsync(Guid runId, Guid packageId, string destinationFile, CancellationToken ct)
    {
        if (Before is not null) await Before();
        await File.WriteAllBytesAsync(destinationFile, Content ?? throw new InvalidOperationException("no content"), ct);
    }
}

public sealed class CollectingReporter : IRunReporter
{
    public List<(string Step, string Status)> Steps { get; } = [];
    public List<(string Level, string Step, string Message)> Logs { get; } = [];
    public void Step(Guid runId, string step, string status, string? detail = null) => Steps.Add((step, status));
    public void Log(Guid runId, string level, string step, string message) => Logs.Add((level, step, message));
    public bool Reached(string step) => Steps.Any(s => s.Step == step && s.Status == "Started");
    public string AllText => string.Join("\n", Logs.Select(l => l.Message));
}

/// <summary>Everything needed to run the executor against a real temporary folder tree.</summary>
public sealed class DeployHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "icc-deploy-" + Guid.NewGuid().ToString("N")[..10]);
    public string Target { get; }
    public AgentDirs Dirs { get; }
    public FakeSites Sites { get; }
    public FakePools Pools { get; } = new();
    public FakeHealth Health { get; } = new();
    public FakeAv Av { get; } = new();
    public FakePlans Plans { get; } = new();
    public FakePackages Packages { get; } = new();
    public ProtectedStore ProtectedStore { get; }
    public BackupStore Backups { get; }
    public RunJournal Journal { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    public ExecutorOptions Options { get; }
    public DeploymentExecutor Executor { get; }
    public FolderInspector Inspector { get; }

    public DeployHarness(Func<long>? freeSpace = null)
    {
        Directory.CreateDirectory(Root);
        Target = Path.Combine(Root, "sites", "pay", "web");
        Directory.CreateDirectory(Target);
        Dirs = new AgentDirs(Path.Combine(Root, "agent"));
        Sites = new FakeSites(Target);
        ProtectedStore = new ProtectedStore(Dirs);
        Backups = new BackupStore(Dirs);
        Journal = new RunJournal(Dirs);
        var allowedRoot = Path.Combine(Root, "sites");
        Options = new ExecutorOptions
        {
            IsPathAllowed = p => Path.GetFullPath(p).StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            CopyRetries = 1, CopyRetryDelay = TimeSpan.FromMilliseconds(5),
            FreeSpaceProbe = freeSpace is null ? null : _ => freeSpace(),
        };
        Executor = new DeploymentExecutor(Dirs, Options, Packages, Health, Sites, Pools, Av, Plans, ProtectedStore, Backups, Journal, Clock, NullLogger<DeploymentExecutor>.Instance);
        Inspector = new FolderInspector(new TargetValidator(Options, Sites), ProtectedStore);
    }

    // ---------- helpers to build a "live" folder and packages ----------

    public void Live(string rel, string content)
    {
        var p = Path.Combine(Target, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }
    public string Read(string rel) => File.ReadAllText(Path.Combine(Target, rel.Replace('/', Path.DirectorySeparatorChar)));
    public bool Exists(string rel) => File.Exists(Path.Combine(Target, rel.Replace('/', Path.DirectorySeparatorChar)));
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Target, f).Replace('\\', '/'), f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, c) in entries) { using var s = z.CreateEntry(n).Open(); s.Write(Encoding.UTF8.GetBytes(c)); }
        return ms.ToArray();
    }

    public static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Builds the command the portal would send, including the snapshot the approver saw (taken from the live folder right now).</summary>
    public DeployCommand Command(byte[] zip, DeployMode mode = DeployMode.Overlay, string[]? forced = null, string[]? dataFolders = null, string[]? configPatterns = null,
        bool scanOnAgent = false, string[]? extraExt = null, int keep = 5, bool takeSnapshot = true, string? packageSha = null, long? packageSize = null)
    {
        Packages.Content = zip;
        var pp = new ProtectedPaths(configPatterns ?? ["web.config", "appsettings*.json"], dataFolders ?? ["uploads"]);
        var files = ZipFiles(zip);
        var snapshot = new List<SnapshotFile>(); var deletes = new List<string>();
        if (takeSnapshot)
        {
            foreach (var f in files)
            {
                if (pp.IsInDataFolder(f.Path)) continue;
                var l = FolderScanner.Probe(Target, f.Path, true);
                snapshot.Add(new SnapshotFile(f.Path, l?.Size ?? 0, l?.Sha256));
            }
            if (mode == DeployMode.FullReplace)
            {
                var live = snapshot.Where(s => s.Sha256 is not null).ToDictionary(s => s.Path, s => new LiveFile(s.Path, s.Size, s.Sha256), StringComparer.OrdinalIgnoreCase);
                var plan = DeployPlanner.Plan(files, live, FolderScanner.All(Target, pp, false), mode, pp, new HashSet<string>(forced ?? [], StringComparer.OrdinalIgnoreCase));
                deletes = plan.Deletes.Select(d => d.Path).ToList();
            }
        }
        var runId = Guid.NewGuid();
        Plans.Plans[runId] = new RunPlanInput(snapshot, deletes);
        return new DeployCommand(runId, Guid.NewGuid(), Guid.NewGuid(), Target, 1, "/", Guid.NewGuid(),
            packageSha ?? Convert.ToHexString(SHA256.HashData(zip)), packageSize ?? zip.Length, mode, pp.ConfigPatterns, pp.DataFolders, forced ?? [],
            keep, "/health", 200, 10, extraExt ?? [], scanOnAgent);
    }

    public static List<PackageFile> ZipFiles(byte[] zip)
    {
        using var z = new ZipArchive(new MemoryStream(zip));
        return z.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e =>
        {
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            return new PackageFile(e.FullName.Replace('\\', '/'), ms.Length, Convert.ToHexString(SHA256.HashData(ms.ToArray())));
        }).ToList();
    }

    public Task<RunCompleted> Deploy(DeployCommand cmd, CollectingReporter? rep = null, CancellationToken ct = default) => Executor.DeployAsync(cmd, rep ?? new CollectingReporter(), ct);

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
