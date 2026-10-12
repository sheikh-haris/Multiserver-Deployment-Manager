using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Contracts;

// ---------- Portal -> Agent (small control messages go over SignalR; big payloads go over HTTPS) ----------

/// <summary>
/// Ask the agent for facts about a target folder. It returns only hashes and sizes, never file contents.
/// The list of package paths to look at can be large, so the agent fetches it over HTTPS by command id.
/// </summary>
public sealed record ManifestRequest(
    string PhysicalPath,
    long IisSiteId,
    string AppPath,
    /// <summary>Full Replace: also list every file on the target (size only) to find what would be deleted.</summary>
    bool ListAll,
    IReadOnlyList<string> DataFolders);

public sealed record ManifestEntry(string Path, long Size, string? Sha256);

/// <summary>Uploaded by the agent over HTTPS (it can be large), keyed by the command id.</summary>
public sealed record ManifestResult(
    Guid CommandId, bool Ok, string? Error,
    IReadOnlyList<ManifestEntry> Existing,
    IReadOnlyList<ManifestEntry> AllFiles,
    int TotalFiles);

/// <summary>Only for protected config files the requester wants to overwrite. The agent masks secrets before it answers.</summary>
public sealed record MaskedFileRequest(string PhysicalPath, long IisSiteId, string AppPath, string RelativePath, string Salt, IReadOnlyList<string> ConfigPatterns);

public sealed record MaskedFileResult(Guid CommandId, bool Ok, string? Error, bool Exists, bool Truncated, string? MaskedText);

public sealed record SnapshotFile(string Path, long Size, string? Sha256);

/// <summary>What the approver saw. Fetched by the agent over HTTPS by run id (it can be large).</summary>
public sealed record RunPlanInput(
    /// <summary>The live state of the package's paths when the request was submitted (null hash = absent).</summary>
    IReadOnlyList<SnapshotFile> Snapshot,
    /// <summary>Full Replace: the files the approver saw as "will be deleted".</summary>
    IReadOnlyList<string> Deletes);

public sealed record DeployCommand(
    Guid RunId,
    Guid RequestId,
    Guid TargetId,
    /// <summary>Must be inside the agent's local roots AND equal the real IIS folder of this site and application.</summary>
    string PhysicalPath,
    long IisSiteId,
    string AppPath,
    Guid PackageId,
    string PackageSha256,
    long PackageSize,
    DeployMode Mode,
    IReadOnlyList<string> ProtectedConfig,
    IReadOnlyList<string> ProtectedData,
    IReadOnlyList<string> ForcedPaths,
    int BackupsToKeep,
    string HealthPath,
    int ExpectedHealthStatus,
    int HealthTimeoutSeconds,
    IReadOnlyList<string> AllowedExtraExtensions,
    /// <summary>When the portal could not scan the package itself, the agent scans it with the server's own antivirus.</summary>
    bool ScanOnAgent);

public sealed record RollbackCommand(
    Guid RunId, Guid RequestId, Guid TargetId, string PhysicalPath, long IisSiteId, string AppPath,
    /// <summary>Id of a backup this agent made. The agent refuses any id it does not know.</summary>
    string BackupId, string HealthPath, int ExpectedHealthStatus, int HealthTimeoutSeconds);

// ---------- Agent -> Portal ----------

public sealed record RunStep(Guid RunId, string Step, string Status, string? Detail);

public sealed record RunLogLine(DateTimeOffset At, string Level, string Step, string Message);

public sealed record RunLog(Guid RunId, IReadOnlyList<RunLogLine> Lines);

public static class RunResults
{
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";                        // failed and nothing to restore, or restore not needed
    public const string RolledBack = "RolledBack";                // failed, backup restored
    public const string RollbackFailed = "RollbackFailed";        // failed AND the restore failed: needs a person
    public const string NeedsReapproval = "NeedsReapproval";      // the live folder changed since the approver looked
}

public sealed record RunCompleted(
    Guid RunId, string Result, string? BackupId, string? BackupPath, string? FailureStep, string? Message,
    int Added, int Replaced, int Deleted, int Skipped, long DurationMs, string? HealthDetail);
