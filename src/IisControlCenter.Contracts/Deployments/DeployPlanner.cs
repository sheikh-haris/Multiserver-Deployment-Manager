namespace IisControlCenter.Contracts.Deployments;

public enum DeployMode { Overlay = 0, FullReplace = 1 }

public enum FileAction
{
    Add,                  // not on the target yet
    Replace,              // different content
    Unchanged,            // same content, nothing to do
    Delete,               // Full Replace only: not in the package
    SkipProtectedConfig,  // config file that already exists on the target
    SkipProtectedData,    // inside a protected data folder: never touched
    ForceOverwrite,       // protected config file the requester chose to overwrite (needs a reason and shows a warning)
}

public sealed record PackageFile(string Path, long Size, string Sha256);
public sealed record LiveFile(string Path, long Size, string? Sha256);

public sealed record PlannedChange(string Path, FileAction Action, long? OldSize, long? NewSize, string? OldHash, string? NewHash);

public sealed record DeployPlan(IReadOnlyList<PlannedChange> Changes)
{
    public int Count(FileAction a) => Changes.Count(c => c.Action == a);
    public IEnumerable<PlannedChange> Writes => Changes.Where(c => c.Action is FileAction.Add or FileAction.Replace or FileAction.ForceOverwrite);
    public IEnumerable<PlannedChange> Deletes => Changes.Where(c => c.Action == FileAction.Delete);
}

/// <summary>
/// One place that decides what happens to every file. The portal uses it for the diff the approver sees;
/// the agent uses it to do the work. Because it is the same code, what was approved is what runs.
/// </summary>
public static class DeployPlanner
{
    /// <param name="package">Every file in the package (relative paths, '/').</param>
    /// <param name="live">Live info for the package paths that exist on the target (hash required for Replace/Unchanged decisions).</param>
    /// <param name="allLive">Every file on the target (outside data folders). Only needed for Full Replace.</param>
    /// <param name="forced">Protected CONFIG files the requester chose to overwrite. Data folders can never be forced.</param>
    public static DeployPlan Plan(
        IEnumerable<PackageFile> package,
        IReadOnlyDictionary<string, LiveFile> live,
        IEnumerable<LiveFile>? allLive,
        DeployMode mode,
        ProtectedPaths protectedPaths,
        IReadOnlySet<string>? forced = null)
    {
        var changes = new List<PlannedChange>();
        var inPackage = new HashSet<string>(RelativePaths.Comparer);

        foreach (var f in package.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            inPackage.Add(f.Path);
            live.TryGetValue(f.Path, out var l);

            if (protectedPaths.IsInDataFolder(f.Path))
            {
                changes.Add(new(f.Path, FileAction.SkipProtectedData, l?.Size, f.Size, l?.Sha256, f.Sha256));
                continue;
            }
            if (protectedPaths.IsConfigFile(f.Path) && l is not null)
            {
                var force = forced is not null && forced.Contains(f.Path, RelativePaths.Comparer);
                changes.Add(new(f.Path, force ? FileAction.ForceOverwrite : FileAction.SkipProtectedConfig, l.Size, f.Size, l.Sha256, f.Sha256));
                continue;
            }
            if (l is null) changes.Add(new(f.Path, FileAction.Add, null, f.Size, null, f.Sha256));
            else if (l.Sha256 is not null && string.Equals(l.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                changes.Add(new(f.Path, FileAction.Unchanged, l.Size, f.Size, l.Sha256, f.Sha256));
            else changes.Add(new(f.Path, FileAction.Replace, l.Size, f.Size, l.Sha256, f.Sha256));
        }

        if (mode == DeployMode.FullReplace && allLive is not null)
        {
            foreach (var l in allLive.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (inPackage.Contains(l.Path)) continue;
                if (protectedPaths.IsInDataFolder(l.Path)) continue;       // data is never deleted (and not even listed)
                if (protectedPaths.IsConfigFile(l.Path)) continue;         // protected config files are kept
                changes.Add(new(l.Path, FileAction.Delete, l.Size, null, l.Sha256, null));
            }
        }
        return new DeployPlan(changes);
    }

    /// <summary>Warning for Full Replace: many deletions often mean the wrong package or a missing data folder.</summary>
    public static string? DeletionWarning(DeployPlan plan, int totalLiveFiles, int absoluteLimit = 20, double fractionLimit = 0.10)
    {
        var deletes = plan.Count(FileAction.Delete);
        if (deletes == 0) return null;
        if (deletes > absoluteLimit || (totalLiveFiles > 0 && deletes > totalLiveFiles * fractionLimit))
            return $"This deployment will DELETE {deletes} files ({(totalLiveFiles > 0 ? (100.0 * deletes / totalLiveFiles).ToString("0") : "?")}% of the folder). " +
                   "Check that this is the right package and that user data folders are in the protected list.";
        return null;
    }
}
