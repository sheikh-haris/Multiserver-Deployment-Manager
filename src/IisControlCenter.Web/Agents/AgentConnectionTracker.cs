using System.Collections.Concurrent;

namespace IisControlCenter.Web.Agents;

/// <summary>In-memory map of which agent is connected on which SignalR connection (single node).</summary>
public sealed class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, string> _connections = new();

    public void Add(Guid serverId, string connectionId) => _connections[serverId] = connectionId;

    /// <summary>Removes only if this connection is still the current one (a reconnect may already have replaced it).</summary>
    public bool Remove(Guid serverId, string connectionId) =>
        _connections.TryGetValue(serverId, out var c) && c == connectionId &&
        _connections.TryRemove(new KeyValuePair<Guid, string>(serverId, connectionId));

    public string? GetConnectionId(Guid serverId) => _connections.TryGetValue(serverId, out var c) ? c : null;
    public bool IsConnected(Guid serverId) => _connections.ContainsKey(serverId);
}
