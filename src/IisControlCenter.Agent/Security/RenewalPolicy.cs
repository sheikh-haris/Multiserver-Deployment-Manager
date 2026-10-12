namespace IisControlCenter.Agent.Security;

/// <summary>When should the agent replace its client certificate?</summary>
public static class RenewalPolicy
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(90);

    /// <summary>A freshly made certificate is never renewed again at once, whatever the window says.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    public static bool ShouldRenew(DateTime notBeforeUtc, DateTime notAfterUtc, DateTime nowUtc, TimeSpan window) =>
        notAfterUtc - nowUtc <= window && nowUtc - notBeforeUtc >= MinimumAge;
}
