using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Targets;

public sealed record VerifyResult(TargetStatus Status, string? Detail, string? Before = null, string? After = null);

/// <summary>Compares a stored link with what IIS reported in the latest discovery.</summary>
public static class TargetVerifier
{
    public static VerifyResult Verify(DeploymentTarget t, IisSite? site)
    {
        if (site is null)
            return new(TargetStatus.Missing, $"IIS site id {t.IisSiteId} ('{t.IisSiteName}') no longer exists.", t.IisSiteName, null);

        if (!string.Equals(site.Name, t.IisSiteName, StringComparison.Ordinal))
            return new(TargetStatus.Missing, $"Site was renamed outside the portal: '{t.IisSiteName}' -> '{site.Name}'.", t.IisSiteName, site.Name);

        var app = site.Applications.FirstOrDefault(a => string.Equals(LinkRules.NormalizeAppPath(a.Path), LinkRules.NormalizeAppPath(t.AppPath), StringComparison.OrdinalIgnoreCase));
        if (app is null)
            return new(TargetStatus.Missing, $"Application '{t.AppPath}' no longer exists on site '{site.Name}'.", t.AppPath, null);

        var changes = new List<string>();
        string before = "", after = "";
        if (!string.Equals(LinkRules.NormalizePath(app.PhysicalPath), LinkRules.NormalizePath(t.PhysicalPath), StringComparison.Ordinal))
        {
            changes.Add($"folder '{t.PhysicalPath}' -> '{app.PhysicalPath}'");
            before += $"path={t.PhysicalPath}; "; after += $"path={app.PhysicalPath}; ";
        }
        if (!string.Equals(app.AppPoolName ?? "", t.AppPoolName, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"app pool '{t.AppPoolName}' -> '{app.AppPoolName}'");
            before += $"pool={t.AppPoolName}"; after += $"pool={app.AppPoolName}";
        }
        if (changes.Count > 0)
            return new(TargetStatus.Drifted, "Changed outside the portal: " + string.Join(", ", changes) + ". Deployments are blocked until an admin accepts or reverts it.", before.Trim(), after.Trim());

        return new(TargetStatus.Active, null);
    }
}
