namespace IisControlCenter.Contracts;

/// <summary>Bump when a breaking change is made to any message below.</summary>
public static class ContractInfo
{
    public const int Version = 4;
    public const int MinimumAgentVersion = 4;   // v2: deployment commands. v3: app pool commands and pool settings in discovery. v4: IIS management commands

    public static class Routes
    {
        public const string AgentHub = "/hubs/agent";
        public const string Register = "/api/agent/register";
        public const string AgentResults = "/api/agent/results";      // manifest inputs and uploads
        public const string AgentRuns = "/api/agent/runs";            // plan input per run
        public const string AgentPackages = "/api/agent/packages";    // package download
    }
}

public static class EnvironmentNames
{
    public const string Dev = "Dev";
    public const string Staging = "Staging";
    public const string Prod = "Prod";
}

public enum ServerMode { ReadOnly = 0, Managed = 1 }

// ---------- Registration (HTTPS, token gated) ----------

public sealed record RegisterRequest(
    string Token,
    string Hostname,
    string DeclaredEnvironment,
    string OsVersion,
    string AgentVersion,
    bool IsDomainJoined,
    string CertificateBase64);   // public part only (DER)

public sealed record RegisterResponse(Guid ServerId, string Thumbprint);

// ---------- Portal -> Agent ----------

/// <summary>Wrapper on every command. The agent checks these fields against its LOCAL config before doing anything.</summary>
public sealed record CommandEnvelope(
    Guid CommandId,
    Guid ServerId,
    string Environment,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string CorrelationId,
    ServerMode Mode);

public interface IAgentClient
{
    Task RunDiscovery(CommandEnvelope envelope);
    Task Ping(CommandEnvelope envelope);
    Task GetManifest(CommandEnvelope envelope, ManifestRequest request);
    Task GetMaskedFile(CommandEnvelope envelope, MaskedFileRequest request);
    Task Deploy(CommandEnvelope envelope, DeployCommand command);
    Task RollbackRun(CommandEnvelope envelope, RollbackCommand command);
    Task PoolAction(CommandEnvelope envelope, PoolActionCommand command);
    Task UpdatePool(CommandEnvelope envelope, UpdatePoolCommand command);
    Task IisOperation(CommandEnvelope envelope, IisOpCommand command);
    /// <summary>Tells the agent its certificate was revoked or the server removed. Agent stops reconnecting.</summary>
    Task Disconnect(string reason);
}

// ---------- Agent -> Portal ----------

public sealed record AgentHello(
    int ContractVersion,
    string AgentVersion,
    string Hostname,
    string OsVersion,
    string DeclaredEnvironment,
    bool LocalAllowManaged,
    IReadOnlyList<string> AllowedRoots,
    DateTimeOffset AgentTime);

public sealed record CommandRejected(Guid CommandId, string Reason);

public sealed record Heartbeat(DateTimeOffset AgentTime, bool IisInstalled);

// ---------- Discovery payload (read only; no passwords, no config contents) ----------

public sealed record DiscoveryResult(
    Guid CommandId,
    DateTimeOffset CollectedAt,
    bool IisInstalled,
    IReadOnlyList<DiscoveredSite> Sites,
    IReadOnlyList<DiscoveredAppPool> AppPools,
    IReadOnlyList<DiscoveredCertificate> Certificates,
    IReadOnlyList<DiscoveredDotNetVersion> DotNetVersions);

public sealed record DiscoveredSite(
    long IisSiteId,
    string Name,
    string State,
    string? PhysicalPath,
    string? AppPoolName,
    IReadOnlyList<DiscoveredBinding> Bindings,
    IReadOnlyList<DiscoveredApplication> Applications);

public sealed record DiscoveredBinding(
    string Protocol,
    string? IpAddress,
    int Port,
    string? Host,
    string? CertThumbprint);

public sealed record DiscoveredApplication(
    string Path,
    string? PhysicalPath,
    string? AppPoolName,
    IReadOnlyList<DiscoveredVirtualDirectory> VirtualDirectories);

public sealed record DiscoveredVirtualDirectory(string Path, string? PhysicalPath);

/// <summary>Deliberately has no password or credential field.</summary>
public sealed record DiscoveredAppPool(
    string Name,
    string IdentityType,
    string? RuntimeVersion,
    string PipelineMode,
    string State)
{
    /// <summary>The settings the portal can change. Filled by agents version 3 and newer.</summary>
    public PoolConfig? Config { get; init; }
}

public sealed record DiscoveredCertificate(
    string Thumbprint,
    string Subject,
    DateTimeOffset NotAfter,
    string Store);

public sealed record DiscoveredDotNetVersion(string Kind, string Version);
