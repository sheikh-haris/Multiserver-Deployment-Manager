namespace IisControlCenter.Core.Domain;

public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Entra group that owns the project. Given the ProjectOwner role when the project is created.</summary>
    public string? OwnerGroupId { get; set; }
    public string? OwnerGroupName { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>1 or 2. Used for direct-to-Production deployments (Phase 3).</summary>
    public int ProdApproversRequired { get; set; } = 1;
    /// <summary>Address (or distribution list) that gets an email when a request needs approval and when it finishes.</summary>
    public string? NotificationEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<Component> Components { get; set; } = new();
}

public class Component
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Type { get; set; } = "Web";
    public string HealthCheckPath { get; set; } = "/";
    /// <summary>Extra file extensions allowed in packages, for example ".exe;.ps1". Empty = none.</summary>
    public string? AllowedFileTypes { get; set; }
    public bool RequiresStagingBeforeProd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<DeploymentTarget> Targets { get; set; } = new();
    public List<ProtectedPath> ProtectedPaths { get; set; } = new();
}

public class DeploymentTarget
{
    public Guid Id { get; set; }
    public Guid ComponentId { get; set; }
    public string EnvironmentName { get; set; } = "";
    public Guid ServerId { get; set; }
    /// <summary>Stable identity: ServerId + IisSiteId + AppPath.</summary>
    public long IisSiteId { get; set; }
    /// <summary>Kept only to notice renames. It is not part of the identity.</summary>
    public string IisSiteName { get; set; } = "";
    public string AppPath { get; set; } = "/";
    public string PhysicalPath { get; set; } = "";
    public string AppPoolName { get; set; } = "";
    public int DeployOrder { get; set; }
    public TargetStatus Status { get; set; } = TargetStatus.Active;
    public string? StatusDetail { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public Guid? LinkExceptionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public class ProtectedPath
{
    public Guid Id { get; set; }
    public Guid ComponentId { get; set; }
    public string Path { get; set; } = "";
    public ProtectedPathKind Kind { get; set; }
    public bool IsDefault { get; set; }
}

public class ProjectEnvironmentSetting
{
    public Guid ProjectId { get; set; }
    public string EnvironmentName { get; set; } = "";
    /// <summary>Decision D5. Default off. Always audited and badged when used.</summary>
    public bool AllowSelfApproval { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class LinkException
{
    public Guid Id { get; set; }
    public string Reason { get; set; } = "";
    public string Violation { get; set; } = "";
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>A Project Owner asks for a link; a Platform Admin decides.</summary>
public class LinkRequest
{
    public Guid Id { get; set; }
    public Guid ComponentId { get; set; }
    public string EnvironmentName { get; set; } = "";
    public Guid ServerId { get; set; }
    public long IisSiteId { get; set; }
    public string AppPath { get; set; } = "/";
    public string Reason { get; set; } = "";
    public LinkRequestStatus Status { get; set; } = LinkRequestStatus.Pending;
    public string RequestedBy { get; set; } = "";
    public string RequestedById { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }
}

public class DriftEvent
{
    public Guid Id { get; set; }
    public Guid ServerId { get; set; }
    /// <summary>Site, AppPool, Certificate or Target.</summary>
    public string ObjectType { get; set; } = "";
    public string ObjectRef { get; set; } = "";
    public DriftKind Kind { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public Guid? TargetId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}
