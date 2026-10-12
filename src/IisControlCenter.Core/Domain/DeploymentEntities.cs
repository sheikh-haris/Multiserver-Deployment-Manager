using IisControlCenter.Core.Deployments;

namespace IisControlCenter.Core.Domain;

public enum AvResult { Pending, Clean, Infected, Error, ScanOnTarget }

public enum UploadStatus { Receiving, Assembling, Validating, Ready, Rejected, Expired }

public enum ApprovalDecision { Approved, Rejected }

public enum NotificationStatus { Pending, Sent, Failed }

public class UploadSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public Guid ComponentId { get; set; }
    public string FileName { get; set; } = "";
    public long TotalSize { get; set; }
    public int ChunkSize { get; set; }
    public int TotalChunks { get; set; }
    public UploadStatus Status { get; set; }
    public string? Error { get; set; }
    /// <summary>Validation errors as JSON text (list of {path,message}).</summary>
    public string? ErrorsJson { get; set; }
    public Guid? PackageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class Package
{
    public Guid Id { get; set; }
    public Guid ComponentId { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Path of the zip inside the package store. The manifest sits next to it as "{StoragePath}.manifest.json".</summary>
    public string StoragePath { get; set; } = "";
    public AvResult AvResult { get; set; } = AvResult.Pending;
    public string? AvDetail { get; set; }
    public DateTimeOffset? AvScannedAt { get; set; }
    public int EntryCount { get; set; }
    public long UncompressedSize { get; set; }
    public string? WarningsJson { get; set; }
    public string UploadedBy { get; set; } = "";
    public string UploadedById { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class DeploymentRequest
{
    public Guid Id { get; set; }
    public RequestType Type { get; set; }
    public Guid ComponentId { get; set; }
    public string EnvironmentName { get; set; } = "";
    public Guid PackageId { get; set; }
    public int Mode { get; set; }
    public string? SourceReference { get; set; }
    public string? Notes { get; set; }
    public bool IsDirectToProd { get; set; }
    public string? DirectToProdReason { get; set; }
    public string? SingleServerReason { get; set; }
    public Guid? PromotedFromRequestId { get; set; }
    /// <summary>For Rollback requests: the run whose backup is restored.</summary>
    public Guid? RollbackOfRunId { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public string? StatusDetail { get; set; }
    public string RequestedBy { get; set; } = "";
    public string RequestedById { get; set; } = "";
    public string? RequestedByEmail { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int ApprovalsRequired { get; set; } = 1;
    public bool SelfApproved { get; set; }
    /// <summary>Ids of the role assignments that allowed the requester to submit. Re-checked when execution starts.</summary>
    public string? SubmitAssignmentIds { get; set; }
    /// <summary>Prepared for release bundles (later). Not used yet.</summary>
    public Guid? ReleaseBundleId { get; set; }
    public int? BundleOrder { get; set; }
}

public class RequestTarget
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid DeploymentTargetId { get; set; }
    public int DeployOrder { get; set; }
    /// <summary>Physical path of the target when the request was submitted. If IIS has since moved, the run is refused.</summary>
    public string PhysicalPathAtSubmit { get; set; } = "";
    /// <summary>JSON: live state of the package paths at submit time and the Full Replace delete list. Compared again by the agent.</summary>
    public string SnapshotJson { get; set; } = "[]";
    public string DeletesJson { get; set; } = "[]";
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public int Unchanged { get; set; }
    public string? DeletionWarning { get; set; }
}

public class ForcedFile
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>Masked before/after text per target is stored as one JSON object: { "<targetId>": { "before": "...", "after": "...", "note": "..." } }.</summary>
    public string? MaskedDiffJson { get; set; }
}

public class Approval
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string ApproverId { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public ApprovalDecision Decision { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public bool IsSelfApproval { get; set; }
    public Guid? AuthorizedByAssignmentId { get; set; }
}

public class DeploymentRun
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid DeploymentTargetId { get; set; }
    public int Sequence { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? BackupId { get; set; }
    public string? BackupPath { get; set; }
    public string? FailureStep { get; set; }
    public string? Message { get; set; }
    public string? HealthDetail { get; set; }
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? CurrentStep { get; set; }
}

public class DeploymentLogLine
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public int Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string Level { get; set; } = "Info";
    public string Step { get; set; } = "";
    public string Message { get; set; } = "";
}

public class Notification
{
    public Guid Id { get; set; }
    public Guid? RequestId { get; set; }
    public string Kind { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A Platform Admin's confirmation that a component's protected data folders are complete. Needed before Full Replace.</summary>
public class ProtectedListConfirmation
{
    public Guid ComponentId { get; set; }
    public string ConfirmedBy { get; set; } = "";
    public DateTimeOffset ConfirmedAt { get; set; }
    /// <summary>Hash of the protected list when it was confirmed. If the list changes, the confirmation is void.</summary>
    public string ListHash { get; set; } = "";
}
