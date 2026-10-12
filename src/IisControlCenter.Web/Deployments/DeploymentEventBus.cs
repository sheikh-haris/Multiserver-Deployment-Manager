namespace IisControlCenter.Web.Deployments;

public sealed record DeploymentChanged(Guid RequestId, Guid? RunId, bool LogOnly);

/// <summary>In-process notifications so open pages (queue, request detail, live log) refresh. Single portal node.</summary>
public sealed class DeploymentEventBus
{
    public event Action<DeploymentChanged>? Changed;
    public void Publish(DeploymentChanged e)
    {
        var h = Changed;
        if (h is null) return;
        foreach (var d in h.GetInvocationList().Cast<Action<DeploymentChanged>>())
        {
            try { d(e); } catch { /* one broken page must not stop the others */ }
        }
    }
}
