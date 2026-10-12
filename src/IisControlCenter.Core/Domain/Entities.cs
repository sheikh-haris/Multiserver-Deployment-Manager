using IisControlCenter.Contracts;

namespace IisControlCenter.Core.Domain;

public class EnvironmentDef
{
    public string Name { get; set; } = "";
    public bool IsProduction { get; set; }
    public int SortOrder { get; set; }
}

public class Server
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Set when the agent registers. Used only for display.</summary>
    public string? Hostname { get; set; }
    /// <summary>Null until an admin approves the server.</summary>
    public string? EnvironmentName { get; set; }
    /// <summary>What the installer declared on the server itself (local config). Approval must match it.</summary>
    public string? DeclaredEnvironment { get; set; }
    public ServerMode Mode { get; set; } = ServerMode.ReadOnly;
    public AgentStatus AgentStatus { get; set; } = AgentStatus.NotRegistered;
    public bool IsDomainJoined { get; set; }
    public string? AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public bool LocalAllowManaged { get; set; }
    /// <summary>Roots the agent reported from its local config (informational).</summary>
    public string? AllowedRoots { get; set; }
    public bool IisInstalled { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastDiscoveryAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public bool IsDeleted { get; set; }
}

public class RegistrationToken
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class AgentCertificate
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string Thumbprint { get; set; } = "";
    public DateTimeOffset NotAfter { get; set; }
    public CertificateStatus Status { get; set; } = CertificateStatus.Active;
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---- Discovery snapshot (replaced on each discovery) ----

public class IisSite
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public long IisSiteId { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string? PhysicalPath { get; set; }
    public string? AppPoolName { get; set; }
    public MappingStatus MappingStatus { get; set; } = MappingStatus.Unassigned;
    public DateTimeOffset DiscoveredAt { get; set; }
    public List<IisBinding> Bindings { get; set; } = new();
    public List<IisApplication> Applications { get; set; } = new();
}

public class IisBinding
{
    public Guid Id { get; set; }
    public Guid IisSiteRowId { get; set; }
    public string Protocol { get; set; } = "";
    public string? IpAddress { get; set; }
    public int Port { get; set; }
    public string? Host { get; set; }
    public string? CertThumbprint { get; set; }
}

public class IisApplication
{
    public Guid Id { get; set; }
    public Guid IisSiteRowId { get; set; }
    public string Path { get; set; } = "/";
    public string? PhysicalPath { get; set; }
    public string? AppPoolName { get; set; }
    public List<IisVirtualDir> VirtualDirs { get; set; } = new();
}

public class IisVirtualDir
{
    public Guid Id { get; set; }
    public Guid IisApplicationRowId { get; set; }
    public string Path { get; set; } = "/";
    public string? PhysicalPath { get; set; }
}

public class IisAppPool
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string Name { get; set; } = "";
    public string IdentityType { get; set; } = "";
    public string? RuntimeVersion { get; set; }
    public string PipelineMode { get; set; } = "";
    public string State { get; set; } = "";
}

public class IisCertificate
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string Thumbprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset NotAfter { get; set; }
    public string Store { get; set; } = "";
}

public class ServerDotNetVersion
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "";
}

// ---- Access ----

public class RoleAssignment
{
    public Guid Id { get; set; }
    public PrincipalType PrincipalType { get; set; }
    /// <summary>Entra object id (group or user).</summary>
    public string PrincipalId { get; set; } = "";
    public string PrincipalName { get; set; } = "";
    public RoleScope Scope { get; set; }
    public Guid? ProjectId { get; set; }
    /// <summary>Reserved for per-component rights later. Always null for now.</summary>
    public Guid? ComponentId { get; set; }
    /// <summary>Null = all environments (admin must pick this explicitly).</summary>
    public string? EnvironmentName { get; set; }
    public Role Role { get; set; }
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

// ---- Audit & settings ----

public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public Guid? ServerId { get; set; }
    public Guid? ProjectId { get; set; }
    public string? EnvironmentName { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string Result { get; set; } = "Success";
    public string? Detail { get; set; }
    public string? CorrelationId { get; set; }
    public string? ClientIp { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SettingKeys
{
    public const string RegistrationTokenMinutes = "Registration.TokenMinutes";
    public const string CertExpiryWarningDays = "Discovery.CertExpiryWarningDays";
    public const string OfflineAfterSeconds = "Agent.OfflineAfterSeconds";
    public const string DiscoveryIntervalHours = "Discovery.IntervalHours";
}
