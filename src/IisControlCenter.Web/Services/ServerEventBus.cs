namespace IisControlCenter.Web.Services;

public enum ServerChangeKind { Status, Discovery, List }

public sealed record ServerChanged(Guid ServerId, ServerChangeKind Kind);

/// <summary>In-process notifications so open pages refresh live. Single portal node.</summary>
public sealed class ServerEventBus
{
    public event Action<ServerChanged>? Changed;
    public void Publish(ServerChanged e) => Changed?.Invoke(e);
}
