using System.Collections.Concurrent;
using IisControlCenter.Contracts;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// Matches a question sent to an agent with its answer. The question list can be large, so the agent fetches it over HTTPS
/// by command id, and sends big answers (the manifest) over HTTPS as well. Only the server the command was sent to may answer.
/// </summary>
public sealed class PendingAgentResults(TimeProvider clock)
{
    private sealed record Entry<T>(Guid ServerId, TaskCompletionSource<T> Tcs, DateTimeOffset Expires, IReadOnlyList<string>? Input);
    private readonly ConcurrentDictionary<Guid, Entry<ManifestResult>> _manifests = new();
    private readonly ConcurrentDictionary<Guid, Entry<MaskedFileResult>> _masked = new();

    public Task<ManifestResult> ExpectManifest(Guid commandId, Guid serverId, IReadOnlyList<string> paths)
    {
        Sweep();
        var e = new Entry<ManifestResult>(serverId, new(TaskCreationOptions.RunContinuationsAsynchronously), clock.GetUtcNow().AddMinutes(10), paths);
        _manifests[commandId] = e;
        return e.Tcs.Task;
    }

    public bool TryGetManifestInput(Guid commandId, Guid serverId, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!_manifests.TryGetValue(commandId, out var e) || e.ServerId != serverId || e.Expires < clock.GetUtcNow()) return false;
        paths = e.Input ?? [];
        return true;
    }

    public bool CompleteManifest(Guid serverId, ManifestResult r)
    {
        if (!_manifests.TryGetValue(r.CommandId, out var e) || e.ServerId != serverId) return false;
        _manifests.TryRemove(r.CommandId, out _);
        return e.Tcs.TrySetResult(r);
    }

    public Task<MaskedFileResult> ExpectMasked(Guid commandId, Guid serverId)
    {
        Sweep();
        var e = new Entry<MaskedFileResult>(serverId, new(TaskCreationOptions.RunContinuationsAsynchronously), clock.GetUtcNow().AddMinutes(10), null);
        _masked[commandId] = e;
        return e.Tcs.Task;
    }

    public bool CompleteMasked(Guid serverId, MaskedFileResult r)
    {
        if (!_masked.TryGetValue(r.CommandId, out var e) || e.ServerId != serverId) return false;
        _masked.TryRemove(r.CommandId, out _);
        return e.Tcs.TrySetResult(r);
    }

    private readonly ConcurrentDictionary<Guid, Entry<PoolOpResult>> _pools = new();

    public Task<PoolOpResult> ExpectPool(Guid commandId, Guid serverId)
    {
        Sweep();
        var e = new Entry<PoolOpResult>(serverId, new(TaskCreationOptions.RunContinuationsAsynchronously), clock.GetUtcNow().AddMinutes(10), null);
        _pools[commandId] = e;
        return e.Tcs.Task;
    }

    public bool CompletePool(Guid serverId, PoolOpResult r)
    {
        if (!_pools.TryGetValue(r.CommandId, out var e) || e.ServerId != serverId) return false;
        _pools.TryRemove(r.CommandId, out _);
        return e.Tcs.TrySetResult(r);
    }

    public void Forget(Guid commandId) { _manifests.TryRemove(commandId, out _); _masked.TryRemove(commandId, out _); _pools.TryRemove(commandId, out _); }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var kv in _manifests.Where(k => k.Value.Expires < now).ToList()) { if (_manifests.TryRemove(kv.Key, out var e)) e.Tcs.TrySetCanceled(); }
        foreach (var kv in _masked.Where(k => k.Value.Expires < now).ToList()) { if (_masked.TryRemove(kv.Key, out var e)) e.Tcs.TrySetCanceled(); }
        foreach (var kv in _pools.Where(k => k.Value.Expires < now).ToList()) { if (_pools.TryRemove(kv.Key, out var e)) e.Tcs.TrySetCanceled(); }
    }
}
