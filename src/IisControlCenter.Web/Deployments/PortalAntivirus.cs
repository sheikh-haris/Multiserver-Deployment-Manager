using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Web.Deployments;

public interface IPortalAntivirus
{
    Task<ScanResult> ScanAsync(string file, CancellationToken ct);
}

/// <summary>Windows Defender on the portal server. If the portal's account may not run it, the result is Unavailable (decision D7).</summary>
public sealed class DefenderPortalAntivirus : IPortalAntivirus
{
    public Task<ScanResult> ScanAsync(string file, CancellationToken ct) => DefenderScanner.ScanFileAsync(file, TimeSpan.FromMinutes(15), ct);
}
