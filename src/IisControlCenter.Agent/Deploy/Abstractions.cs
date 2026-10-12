using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Agent.Iis;

namespace IisControlCenter.Agent.Deploy;

public interface IRunReporter
{
    void Step(Guid runId, string step, string status, string? detail = null);
    void Log(Guid runId, string level, string step, string message);
}

public interface IPackageSource
{
    /// <summary>Downloads the package for this run to <paramref name="destinationFile"/>.</summary>
    Task DownloadAsync(Guid runId, Guid packageId, string destinationFile, CancellationToken ct);
}

public sealed record HealthResult(bool Ok, string Detail);

public interface IHealthChecker
{
    Task<HealthResult> CheckAsync(IReadOnlyList<DiscoveredBinding> bindings, string path, int expectedStatus, TimeSpan timeout, CancellationToken ct);
}

public interface IRunPlanSource
{
    Task<RunPlanInput> GetAsync(Guid runId, CancellationToken ct);
}

public interface IManifestInputSource
{
    Task<IReadOnlyList<string>> GetPathsAsync(Guid commandId, CancellationToken ct);
}

public interface IAgentAntivirus
{
    Task<ScanResult> ScanAsync(string file, CancellationToken ct);
}

public static class Steps
{
    public const string Validate = "Validate";
    public const string Download = "Download";
    public const string Scan = "Scan";
    public const string Extract = "Extract";
    public const string Snapshot = "Snapshot";
    public const string Backup = "Backup";
    public const string Offline = "Offline";
    public const string Copy = "Copy";
    public const string Restart = "Restart";
    public const string Online = "Online";
    public const string Health = "Health";
    public const string Rollback = "Rollback";
    public const string Cleanup = "Cleanup";
}

/// <summary>Raised for an expected, explained failure of one step.</summary>
public sealed class StepFailedException(string step, string message, string result = RunResults.Failed) : Exception(message)
{
    public string Step { get; } = step;
    public string Result { get; } = result;
}
