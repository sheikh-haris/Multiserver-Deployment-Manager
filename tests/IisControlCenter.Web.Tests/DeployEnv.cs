using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static IisControlCenter.Web.Tests.TestEnv;

namespace IisControlCenter.Web.Tests;

public sealed class FakeAntivirus : IPortalAntivirus
{
    public ScanResult Result { get; set; } = new(ScanOutcome.Clean, "fake");
    public Task<ScanResult> ScanAsync(string file, CancellationToken ct) => Task.FromResult(Result);
}

public sealed class FakeEmail : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public bool Fail { get; set; }
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("smtp down");
        Sent.Add((to, subject, body)); return Task.CompletedTask;
    }
}

/// <summary>Everything Phase 3 needs: services over the test database, a temp package store, and a fake agent that answers like the real one.</summary>
public sealed class DeployEnv : IDisposable
{
    public TestEnv E { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "icc-web-" + Guid.NewGuid().ToString("N"));
    public FilePackageStore Store { get; }
    public FakeAntivirus Av { get; } = new();
    public FakeEmail Email { get; } = new();
    public DeploymentOptions Options { get; } = new() { ChunkMb = 1, MaxPackageMb = 20, RunTimeoutMinutes = 30, AgentOfflineGraceMinutes = 10, PublicBaseUrl = "https://portal.test" };
    public PendingAgentResults Pending { get; }
    public DeploymentEventBus Bus { get; } = new();
    public UploadService Uploads { get; }
    public DiffService Diff { get; }
    public ProtectedListService Protected { get; }
    public NotificationService Notify { get; }
    public DeploymentService Svc { get; }
    public DeploymentOrchestrator Orch { get; }
    public RunReportService Reports { get; }
    public MaintenanceService Maintenance { get; }

    // fake world
    public Guid ProjectId { get; private set; }
    public Guid ComponentId { get; private set; }
    public Guid ServerId { get; private set; }
    public Guid TargetId { get; private set; }
    public Dictionary<Guid, Dictionary<string, string>> LiveByServer { get; } = new();
    public Dictionary<string, string> Live => LiveByServer[ServerId];
    /// <summary>What the fake agent answers to a Deploy command. Default: success.</summary>
    public Func<DeployCommand, RunCompleted>? DeployOutcome { get; set; }
    public bool AutoRunDeploy { get; set; } = true;
    public bool AutoRunRollback { get; set; } = true;
    public Func<RollbackCommand, RunCompleted>? RollbackOutcome { get; set; }
    public List<RunLog> Logs { get; } = [];

    public DeployEnv()
    {
        Directory.CreateDirectory(Root);
        Store = new FilePackageStore(Path.Combine(Root, "store"));
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        Pending = new PendingAgentResults(E.Clock);
        Diff = new DiffService(Store, E.Commands, Pending, opts);
        Protected = new ProtectedListService(E.Db, E.Audit, E.Clock);
        Notify = new NotificationService(E.Db, Email, opts, E.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance);
        Svc = new DeploymentService(E.Db, Diff, Store, Protected, E.Audit, Notify, Bus, opts, E.Clock);
        Orch = new DeploymentOrchestrator(E.Db, E.Commands, E.Audit, Notify, Bus, opts, E.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeploymentOrchestrator>.Instance);
        Reports = new RunReportService(E.Db, Orch, E.Audit, Bus, E.Clock);
        Uploads = new UploadService(E.Db, Store, Av, opts, E.Audit, E.Clock);
        Maintenance = new MaintenanceService(E.Db, Svc, Uploads, Store, E.Audit, E.Clock);

        E.Hub.Client.OnManifest = (env, req) => AnswerManifest(env, req);
        E.Hub.Client.OnMaskedFile = (env, req) => AnswerMasked(env, req);
        E.Hub.Client.OnDeploy = async (env, cmd) =>
        {
            if (!AutoRunDeploy) return;
            await Reports.LogAsync(env.ServerId, new RunLog(cmd.RunId, [new RunLogLine(E.Clock.GetUtcNow(), "Info", "Validate", "starting")]));
            await Reports.CompletedAsync(env.ServerId, (DeployOutcome ?? Success)(cmd));
        };
        E.Hub.Client.OnRollback = async (env, cmd) =>
        {
            if (!AutoRunRollback) return;
            await Reports.CompletedAsync(env.ServerId, (RollbackOutcome ?? (c => new RunCompleted(c.RunId, RunResults.Succeeded, null, null, null, "restored", 0, 3, 0, 0, 1000, "ok")))(cmd));
        };
    }

    public static RunCompleted Success(DeployCommand c) => new(c.RunId, RunResults.Succeeded, "bk-" + c.RunId.ToString("N")[..8], @"D:\Backups\x", null, "done", 2, 3, 0, 1, 1500, "GET / -> 200");

    /// <summary>Project "Payments", component "Web", environment server with a linked site, plus a Deployer and two Approvers.</summary>
    public async Task<DeployEnv> SetUp(string env = "Dev")
    {
        ProjectId = await E.NewProject();
        ComponentId = await E.NewComponent(ProjectId);
        (ServerId, TargetId) = await AddServer("WEB01", env, 11);
        return this;
    }

    public async Task<(Guid server, Guid target)> AddServer(string name, string env, long siteId)
    {
        var s = await E.ApprovedServer(name, env);
        await E.Store(s, new SiteSpec(siteId, name + "-site", @"D:\Websites\" + name, name + "Pool"));
        var t = await E.Link(ComponentId, s, siteId);
        E.Tracker.Add(s, "conn-" + name);
        LiveByServer[s] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return (s, t);
    }

    // ---------- people ----------

    public Actor Person(string id, params (Role Role, string? Env)[] roles)
    {
        using var db = E.Db.CreateDbContext();
        var list = new List<RoleAssignment>();
        foreach (var (role, env) in roles)
        {
            var a = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, PrincipalName = id, Scope = RoleScope.Project, ProjectId = ProjectId, Role = role, EnvironmentName = env, GrantedBy = "test", GrantedAt = E.Clock.GetUtcNow() };
            db.RoleAssignments.Add(a); list.Add(a);
        }
        db.SaveChanges();
        return new Actor(id, "User " + id, "10.0.0.2", new AccessEvaluator(new UserContext(id, "User " + id, []), list, E.Clock.GetUtcNow()), id + "@corp.test");
    }

    public Actor Deployer(string env, string id = "dep") => Person(id, (Role.Deployer, env));
    public Actor Approver(string env, string id = "app") => Person(id, (Role.Approver, env));

    public async Task RevokeAllFor(string principalId)
    {
        await using var db = E.Db.CreateDbContext();
        foreach (var a in await db.RoleAssignments.Where(x => x.PrincipalId == principalId).ToListAsync()) a.RevokedAt = E.Clock.GetUtcNow();
        await db.SaveChangesAsync();
    }

    public async Task AllowSelfApproval(string env, bool allow = true)
    {
        await using var db = E.Db.CreateDbContext();
        db.ProjectEnvironmentSettings.Add(new ProjectEnvironmentSetting { ProjectId = ProjectId, EnvironmentName = env, AllowSelfApproval = allow, UpdatedAt = E.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    public async Task SetProdApprovers(int n)
    {
        await using var db = E.Db.CreateDbContext();
        (await db.Projects.FirstAsync(p => p.Id == ProjectId)).ProdApproversRequired = n;
        await db.SaveChangesAsync();
    }

    // ---------- packages ----------

    public static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (p, c) in files)
            {
                var e = z.CreateEntry(p);
                using var s = e.Open(); s.Write(Encoding.UTF8.GetBytes(c));
            }
        return ms.ToArray();
    }

    public async Task<Package> Upload(Actor by, string env, byte[] zip, string name = "build-1.zip")
    {
        var start = await Uploads.StartAsync(by, ComponentId, env, name, zip.Length);
        Assert.True(start.Ok, start.Error);
        var chunk = start.Value!.ChunkSize;
        for (var i = 0; i < start.Value.TotalChunks; i++)
        {
            var part = zip.Skip(i * chunk).Take(chunk).ToArray();
            var r = await Uploads.ReceiveChunkAsync(by, start.Value.SessionId, i, new MemoryStream(part), Convert.ToHexString(SHA256.HashData(part)), default);
            Assert.True(r.Ok, r.Error);
        }
        var done = await Uploads.CompleteAsync(by, start.Value.SessionId);
        Assert.True(done.Ok, done.Error);
        await Uploads.ProcessPendingAsync();
        var info = await Uploads.GetAsync(by, start.Value.SessionId);
        Assert.Equal(UploadStatus.Ready, info!.Status);
        await using var db = E.Db.CreateDbContext();
        return await db.Packages.AsNoTracking().FirstAsync(p => p.Id == info.PackageId);
    }

    public Task<Package> Upload(Actor by, string env, params (string Path, string Content)[] files) => Upload(by, env, Zip(files));

    public SubmitCommand Cmd(Package p, string env, IEnumerable<Guid>? targets = null, DeployMode mode = DeployMode.Overlay, string? source = "PR-1",
        string? directReason = null, string? singleReason = null, IReadOnlyList<ForcedFileInput>? forced = null, DateTimeOffset? at = null) =>
        new(ComponentId, env, p.Id, (targets ?? [TargetId]).ToList(), mode, source, null, directReason, singleReason, forced ?? [], at);

    /// <summary>Submit and approve (by a different person) so the orchestrator can run it.</summary>
    public async Task<Guid> SubmitApproved(Actor dep, Actor appr, Package p, string env, SubmitCommand? cmd = null)
    {
        var r = await Svc.SubmitAsync(dep, cmd ?? Cmd(p, env));
        Assert.True(r.Ok, r.Error);
        var a = await Svc.ApproveAsync(appr, r.Value);
        Assert.True(a.Ok, a.Error);
        return r.Value;
    }

    public async Task<Guid> SubmitApprovedAndNothing(Actor dep, Actor appr, Package p, string env) => await SubmitApproved(dep, appr, p, env);

    /// <summary>Submit, approve and let the orchestrator run it with the fake agent.</summary>
    public async Task<Guid> SubmitApprovedAndRun(Actor dep, Actor appr, Package p, string env, SubmitCommand? cmd = null)
    {
        var id = await SubmitApproved(dep, appr, p, env, cmd);
        await Orch.TickAsync();
        return id;
    }

    public async Task<DeploymentRequest> Req(Guid id) { await using var db = E.Db.CreateDbContext(); return await db.DeploymentRequests.AsNoTracking().FirstAsync(r => r.Id == id); }
    public async Task<List<DeploymentRun>> Runs(Guid id) { await using var db = E.Db.CreateDbContext(); return await db.DeploymentRuns.AsNoTracking().Where(r => r.RequestId == id).OrderBy(r => r.Sequence).ToListAsync(); }
    public async Task<List<string>> AuditActions(string? result = null) { await using var db = E.Db.CreateDbContext(); return await db.AuditLog.Where(a => result == null || a.Result == result).OrderBy(a => a.Id).Select(a => a.Action + ":" + a.Result).ToListAsync(); }

    // ---------- fake agent ----------

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private Task AnswerManifest(CommandEnvelope env, ManifestRequest req)
    {
        Pending.TryGetManifestInput(env.CommandId, env.ServerId, out var paths);
        var live = LiveByServer[env.ServerId];
        var existing = paths.Where(live.ContainsKey).Select(p => new ManifestEntry(p, live[p].Length, Sha(live[p]))).ToList();
        var all = req.ListAll ? live.Keys.Select(p => new ManifestEntry(p, live[p].Length, null)).ToList() : [];
        Pending.CompleteManifest(env.ServerId, new ManifestResult(env.CommandId, true, null, existing, all, live.Count));
        return Task.CompletedTask;
    }

    private Task AnswerMasked(CommandEnvelope env, MaskedFileRequest req)
    {
        var live = LiveByServer[env.ServerId];
        var exists = live.TryGetValue(req.RelativePath, out var text);
        Pending.CompleteMasked(env.ServerId, new MaskedFileResult(env.CommandId, true, null, exists, false, exists ? SecretMasker.Mask(text!, req.Salt) : null));
        return Task.CompletedTask;
    }

    public void Dispose() { E.Dispose(); try { Directory.Delete(Root, true); } catch { } }
}
