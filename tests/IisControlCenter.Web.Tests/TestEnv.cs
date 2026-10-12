using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;

namespace IisControlCenter.Web.Tests;

public sealed class RecordingAgentClient : IAgentClient
{
    public List<CommandEnvelope> Discoveries { get; } = [];
    public List<string> Disconnects { get; } = [];
    public Task RunDiscovery(CommandEnvelope e) { Discoveries.Add(e); return Task.CompletedTask; }
    public Task Ping(CommandEnvelope e) => Task.CompletedTask;
    public Task Disconnect(string reason) { Disconnects.Add(reason); return Task.CompletedTask; }
}

public sealed class FakeHubContext : IHubContext<AgentHub, IAgentClient>
{
    public RecordingAgentClient Client { get; } = new();
    public IHubClients<IAgentClient> Clients => new FakeClients(Client);
    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class FakeClients(IAgentClient c) : IHubClients<IAgentClient>
    {
        public IAgentClient All => c;
        public IAgentClient AllExcept(IReadOnlyList<string> x) => c;
        public IAgentClient Client(string id) => c;
        public IAgentClient Clients(IReadOnlyList<string> ids) => c;
        public IAgentClient Group(string g) => c;
        public IAgentClient GroupExcept(string g, IReadOnlyList<string> x) => c;
        public IAgentClient Groups(IReadOnlyList<string> g) => c;
        public IAgentClient User(string u) => c;
        public IAgentClient Users(IReadOnlyList<string> u) => c;
    }
}

public sealed class TestDbFactory(SqliteConnection conn) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
}

/// <summary>A complete set of services over an in-memory SQLite database and a fake clock.</summary>
public sealed class TestEnv : IDisposable
{
    public SqliteConnection Conn { get; } = new("Data Source=:memory:");
    public TestDbFactory Db { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
    public FakeHubContext Hub { get; } = new();
    public AgentConnectionTracker Tracker { get; } = new();
    public ServerEventBus Bus { get; } = new();
    public AuditService Audit { get; }
    public AgentCommandService Commands { get; }
    public Reconciler Reconciler { get; }
    public ProjectService Projects { get; }
    public MembershipService Members { get; }
    public TargetService Targets { get; }
    public DriftQueries Drift { get; }
    public ServerService Servers { get; }
    public ServerQueries Queries { get; }
    public RoleService Roles { get; }
    public AuditQueries AuditQueries { get; }
    public List<ServerChanged> Events { get; } = [];

    public TestEnv()
    {
        Conn.Open();
        Db = new TestDbFactory(Conn);
        using (var db = Db.CreateDbContext()) db.Database.EnsureCreated();
        Audit = new AuditService(Db, Clock);
        Commands = new AgentCommandService(Db, Hub, Tracker, new MemoryCache(new MemoryCacheOptions()), Audit, Clock);
        Reconciler = new Reconciler(Db, Audit, Bus, Clock);
        Servers = new ServerService(Db, Audit, Tracker, Commands, Bus, Reconciler, Clock);
        Projects = new ProjectService(Db, Audit, Clock);
        Members = new MembershipService(Db, Audit, Clock);
        Targets = new TargetService(Db, Audit, Reconciler, Bus, Clock);
        Drift = new DriftQueries(Db, Audit, Clock);
        Queries = new ServerQueries(Db, Clock);
        Roles = new RoleService(Db, Audit, Clock);
        AuditQueries = new AuditQueries(Db, Audit);
        Bus.Changed += Events.Add;
    }

    public Actor Admin => ActorWith(Role.PlatformAdmin);
    public Actor Nobody => ActorWith();

    public Actor ActorWith(Role? role = null, string? env = null, string id = "u1")
    {
        var user = new UserContext(id, "User " + id, []);
        var list = role is null ? [] : new List<RoleAssignment>
        {
            new() { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = id, Scope = RoleScope.Platform, Role = role.Value, EnvironmentName = env },
        };
        return new Actor(id, user.DisplayName, "10.0.0.1", new AccessEvaluator(user, list, Clock.GetUtcNow()));
    }

    /// <summary>Creates a self-signed agent certificate. Returns the public DER and the SHA-256 thumbprint.</summary>
    public (string Base64, string Thumbprint, X509Certificate2 Cert) NewCert(int keyBits = 2048, TimeSpan? lifetime = null, DateTimeOffset? notBefore = null)
    {
        using var rsa = RSA.Create(keyBits);
        var req = new CertificateRequest("CN=test-agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var nb = notBefore ?? Clock.GetUtcNow().AddMinutes(-5);
        var cert = req.CreateSelfSigned(nb, nb + (lifetime ?? TimeSpan.FromDays(700)));
        var pub = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        return (Convert.ToBase64String(pub.RawData), ServerService.Thumbprint(pub), cert);
    }

    public RegisterRequest Request(string token, string cert, string env = "Dev") =>
        new(token, "WEB01", env, "Windows Server 2022", "0.1.0", false, cert);

    /// <summary>Creates a server and registers an agent for it. Leaves it Pending.</summary>
    public async Task<(Guid ServerId, string Thumbprint)> RegisteredServer(string name = "WEB01", string env = "Dev")
    {
        var created = await Servers.CreateAsync(Admin, name);
        var (b64, thumb, _) = NewCert();
        var r = await Servers.RegisterAgentAsync(Request(created.Value!.Token, b64, env), "10.1.1.1", thumb);
        Assert.True(r.Ok, r.Error);
        return (created.Value.ServerId, thumb);
    }

    public async Task<Guid> ApprovedServer(string name = "WEB01", string env = "Dev")
    {
        var (id, _) = await RegisteredServer(name, env);
        var r = await Servers.ApproveAsync(Admin, id, env);
        Assert.True(r.Ok, r.Error);
        return id;
    }

    // ---------- Phase 2 helpers ----------

    public sealed record SiteSpec(long Id, string Name, string Path, string Pool, params (string AppPath, string Path, string Pool)[] SubApps);

    public static DiscoveryResult Discovery(params SiteSpec[] sites) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, true,
        sites.Select(s => new DiscoveredSite(s.Id, s.Name, "Started", s.Path, s.Pool,
            [new DiscoveredBinding("http", "*", 80, s.Name.ToLowerInvariant() + ".local", null)],
            new[] { new DiscoveredApplication("/", s.Path, s.Pool, [new DiscoveredVirtualDirectory("/", s.Path)]) }
                .Concat(s.SubApps.Select(a => new DiscoveredApplication(a.AppPath, a.Path, a.Pool, [new DiscoveredVirtualDirectory("/", a.Path)]))).ToList())).ToList(),
        sites.SelectMany(s => s.SubApps.Select(a => a.Pool).Append(s.Pool)).Distinct().Select(p => new DiscoveredAppPool(p, "ApplicationPoolIdentity", "v4.0", "Integrated", "Started")).ToList(),
        [], []);

    public async Task Store(Guid serverId, params SiteSpec[] sites)
    {
        var r = await Servers.StoreDiscoveryAsync(serverId, Discovery(sites));
        Assert.True(r.Ok, r.Error);
        Clock.Advance(TimeSpan.FromMinutes(1));
    }

    public async Task<Guid> NewProject(string name = "Payments", string? ownerGroup = null)
    {
        var r = await Projects.CreateAsync(Admin, name, null, ownerGroup, ownerGroup is null ? null : "Owners of " + name);
        Assert.True(r.Ok, r.Error);
        return r.Value;
    }

    public async Task<Guid> NewComponent(Guid projectId, string name = "Web Frontend")
    {
        var r = await Projects.AddComponentAsync(Admin, projectId, name, "Web", "/health", null, false);
        Assert.True(r.Ok, r.Error);
        return r.Value;
    }

    public async Task<Guid> Link(Guid componentId, Guid serverId, long siteId, string appPath = "/", string? exception = null)
    {
        var r = await Targets.LinkAsync(Admin, componentId, serverId, siteId, appPath, exception);
        Assert.True(r.Ok, r.Error);
        return r.Value;
    }

    public void Dispose() => Conn.Dispose();
}
