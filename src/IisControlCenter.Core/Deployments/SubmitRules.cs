using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Core.Deployments;

public sealed record ForcedFileInput(string Path, string? Reason);

public sealed record SubmitInput(
    RequestType Type,
    bool IsProduction,
    bool IsDirectToProd,
    bool IsPromote,
    bool ComponentRequiresStagingBeforeProd,
    string? SourceReference,
    string? Notes,
    string? DirectToProdReason,
    string? SingleServerReason,
    int ActiveTargetsInEnvironment,
    int SelectedTargets,
    bool PackageClean,
    DeployMode Mode,
    bool ProtectedListConfirmed,
    bool HasProtectedDataFolders,
    IReadOnlyList<ForcedFileInput> ForcedFiles,
    IReadOnlyCollection<string> ExistingProtectedConfigPaths);

/// <summary>All the business rules for submitting a deployment request, in one testable place.</summary>
public static class SubmitRules
{
    public const int MaxSourceReference = 200;
    public const int MaxNotes = 4000;

    public static List<string> Validate(SubmitInput i)
    {
        var e = new List<string>();
        if (i.SelectedTargets < 1) e.Add("Choose at least one target server.");
        if (i.Type == RequestType.Deploy)
        {
            if (string.IsNullOrWhiteSpace(i.SourceReference)) e.Add("A source reference (commit, branch or work item number) is required.");
            else if (i.SourceReference.Length > MaxSourceReference) e.Add($"The source reference is too long (max {MaxSourceReference}).");
        }
        if (i.Notes is { Length: > MaxNotes }) e.Add($"The notes are too long (max {MaxNotes}).");
        if (!i.PackageClean) e.Add("The package has not passed validation and the virus scan.");

        if (i.SelectedTargets >= 1 && i.SelectedTargets < i.ActiveTargetsInEnvironment && string.IsNullOrWhiteSpace(i.SingleServerReason))
            e.Add("Deploying to only some of the servers leaves load-balanced servers running different code. Write the reason.");

        if (i.IsDirectToProd)
        {
            if (!i.IsProduction) e.Add("Direct to Production only applies to the Production environment.");
            if (string.IsNullOrWhiteSpace(i.DirectToProdReason)) e.Add("A reason is required for a direct-to-Production deployment.");
            if (i.ComponentRequiresStagingBeforeProd) e.Add("This component must be deployed to Staging first. Use Promote.");
        }
        if (i.IsPromote && !i.IsProduction) e.Add("Promote deploys the Staging package to Production.");

        if (i.Mode == DeployMode.FullReplace && !i.ProtectedListConfirmed)
            e.Add("Full Replace is blocked until a Platform Admin has confirmed this component's protected data folders (even if the answer is none).");

        var existing = new HashSet<string>(i.ExistingProtectedConfigPaths, RelativePaths.Comparer);
        var seen = new HashSet<string>(RelativePaths.Comparer);
        foreach (var f in i.ForcedFiles)
        {
            if (!seen.Add(f.Path)) { e.Add($"'{f.Path}' is listed twice."); continue; }
            if (string.IsNullOrWhiteSpace(f.Reason)) e.Add($"Force overwrite of '{f.Path}' needs a reason.");
            if (!existing.Contains(f.Path)) e.Add($"'{f.Path}' is not an existing protected config file, so it cannot be force-overwritten.");
        }
        return e;
    }
}
