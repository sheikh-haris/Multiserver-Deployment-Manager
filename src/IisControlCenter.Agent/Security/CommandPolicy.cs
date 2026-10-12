using IisControlCenter.Agent.Config;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Security;

/// <summary>Every command is checked against the LOCAL config before anything runs.</summary>
public sealed class CommandPolicy(AgentConfig config, TimeProvider clock)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly Queue<Guid> _recent = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Lock _gate = new();

    /// <summary>Returns null when the command is acceptable; otherwise the reason it is refused.</summary>
    public string? Validate(CommandEnvelope e)
    {
        var now = clock.GetUtcNow();
        if (e.ServerId != config.ServerId) return "Command is for a different server.";
        if (!string.Equals(e.Environment, config.Environment, StringComparison.OrdinalIgnoreCase))
            return $"Command is for environment '{e.Environment}', this agent belongs to '{config.Environment}'.";
        if (e.ExpiresAt <= now) return "Command has expired.";
        if (e.IssuedAt > now + ClockSkew) return "Command was issued in the future (clock problem).";
        if (e.ExpiresAt - e.IssuedAt > TimeSpan.FromHours(1)) return "Command lifetime is too long.";

        lock (_gate)
        {
            if (!_seen.Add(e.CommandId)) return "Duplicate command.";
            _recent.Enqueue(e.CommandId);
            while (_recent.Count > 1000) _seen.Remove(_recent.Dequeue());
        }
        return null;
    }

    /// <summary>The portal can ask for Managed, but the local config has the last word.</summary>
    public ServerMode EffectiveMode(ServerMode requested) =>
        requested == ServerMode.Managed && config.AllowManaged ? ServerMode.Managed : ServerMode.ReadOnly;
}
