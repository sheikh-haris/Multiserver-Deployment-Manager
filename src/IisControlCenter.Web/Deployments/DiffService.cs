using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Web.Agents;

namespace IisControlCenter.Web.Deployments;

public sealed record TargetDiff(
    Guid TargetId, string ServerName, string PhysicalPath, bool Ok, string? Error,
    IReadOnlyList<PlannedChange> Changes, IReadOnlyList<SnapshotFile> Snapshot, IReadOnlyList<string> Deletes,
    string? DeletionWarning, int TotalLiveFiles)
{
    public int Count(FileAction a) => Changes.Count(c => c.Action == a);
    public static TargetDiff Failed(DeploymentTarget t, string serverName, string error) =>
        new(t.Id, serverName, t.PhysicalPath, false, error, [], [], [], null, 0);
}

public sealed record MaskedFileDiff(string Path, bool Ok, string? Error, string? Before, string? After, bool Truncated, IReadOnlyList<DiffLine>? Lines, string? Note);

/// <summary>
/// Builds the "what will change" list for one target. The agent only reports hashes and sizes of the package's paths
/// (and, for Full Replace, the names of all files); the portal compares them with the package using the same
/// <see cref="DeployPlanner"/> the agent uses to do the work.
/// </summary>
public sealed class DiffService(
    IPackageStore store,
    AgentCommandService commands,
    PendingAgentResults pending)
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(90);

    public static ProtectedPaths ProtectedFor(IEnumerable<ProtectedPath> list) =>
        new(list.Where(p => p.Kind == ProtectedPathKind.ConfigFile).Select(p => p.Path),
            list.Where(p => p.Kind == ProtectedPathKind.DataFolder).Select(p => p.Path));

    public async Task<TargetDiff> ComputeAsync(
        Package pkg, Server server, DeploymentTarget target, DeployMode mode, ProtectedPaths prot, IReadOnlySet<string> forced, CancellationToken ct = default)
    {
        if (server.EnvironmentName is null) return TargetDiff.Failed(target, server.Name, "The server is not approved.");
        var files = store.ReadManifest(pkg);
        var paths = files.Select(f => f.Path).ToList();

        var env = commands.NewEnvelope(server);
        var wait = pending.ExpectManifest(env.CommandId, server.Id, paths);
        var sent = await commands.SendManifestAsync(server, env, new ManifestRequest(target.PhysicalPath, target.IisSiteId, target.AppPath, mode == DeployMode.FullReplace, prot.DataFolders));
        if (!sent.Ok) { pending.Forget(env.CommandId); return TargetDiff.Failed(target, server.Name, sent.Error!); }

        ManifestResult r;
        try { r = await wait.WaitAsync(AgentTimeout, ct); }
        catch (TimeoutException) { pending.Forget(env.CommandId); return TargetDiff.Failed(target, server.Name, $"The agent on {server.Name} did not answer in time."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return TargetDiff.Failed(target, server.Name, "The agent did not answer in time."); }
        if (!r.Ok) return TargetDiff.Failed(target, server.Name, r.Error ?? "The agent could not read the folder.");

        var live = r.Existing.ToDictionary(e => e.Path, e => new LiveFile(e.Path, e.Size, e.Sha256), RelativePaths.Comparer);
        var all = mode == DeployMode.FullReplace ? r.AllFiles.Select(e => new LiveFile(e.Path, e.Size, e.Sha256)).ToList() : null;
        var plan = DeployPlanner.Plan(files, live, all, mode, prot, forced);
        var snapshot = files.Select(f => live.TryGetValue(f.Path, out var l) ? new SnapshotFile(f.Path, l.Size, l.Sha256) : new SnapshotFile(f.Path, 0, null)).ToList();
        var deletes = plan.Deletes.Select(d => d.Path).ToList();
        var warning = mode == DeployMode.FullReplace ? DeployPlanner.DeletionWarning(plan, r.TotalFiles) : null;
        return new TargetDiff(target.Id, server.Name, target.PhysicalPath, true, null, plan.Changes, snapshot, deletes, warning, r.TotalFiles);
    }

    /// <summary>Before/after text of a protected config file, with secrets masked. The live file is masked by the agent; secrets never leave the server.</summary>
    public async Task<MaskedFileDiff> MaskedAsync(Package pkg, Server server, DeploymentTarget target, string path, ProtectedPaths prot, CancellationToken ct = default)
    {
        const int Max = 256 * 1024;
        var salt = Guid.NewGuid().ToString("N");
        var env = commands.NewEnvelope(server);
        var wait = pending.ExpectMasked(env.CommandId, server.Id);
        var sent = await commands.SendMaskedFileAsync(server, env, new MaskedFileRequest(target.PhysicalPath, target.IisSiteId, target.AppPath, path, salt, prot.ConfigPatterns));
        if (!sent.Ok) { pending.Forget(env.CommandId); return new(path, false, sent.Error, null, null, false, null, null); }
        MaskedFileResult r;
        try { r = await wait.WaitAsync(AgentTimeout, ct); }
        catch (TimeoutException) { pending.Forget(env.CommandId); return new(path, false, "The agent did not answer in time.", null, null, false, null, null); }
        if (!r.Ok) return new(path, false, r.Error, null, null, false, null, null);

        var after = store.ReadEntryText(pkg, path, Max, out var trunc2);
        var afterMasked = after is null ? null : SecretMasker.Mask(after, salt);
        var truncated = r.Truncated || trunc2;
        var lines = r.MaskedText is not null && afterMasked is not null ? TextDiff.Diff(r.MaskedText, afterMasked) : null;
        var note = truncated ? "The file is large; only the first part is compared." : lines is null && r.MaskedText is not null ? "The file is too large to show a line diff." : null;
        return new(path, true, null, r.MaskedText, afterMasked, truncated, lines, note);
    }

    public static string ToJson<T>(T v) => JsonSerializer.Serialize(v);
    public static T FromJson<T>(string? json, T fallback) => string.IsNullOrEmpty(json) ? fallback : JsonSerializer.Deserialize<T>(json) ?? fallback;
}
