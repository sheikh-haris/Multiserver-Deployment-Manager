using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// From the last discovery: the folders of other applications and virtual directories of the same server that sit inside this target's folder.
/// They are protected during deployments to the parent (see <see cref="NestedFolders"/>). The agent works this out again from IIS itself.
/// </summary>
public static class NestedFolderQuery
{
    public static async Task<IReadOnlyList<string>> ForAsync(AppDbContext db, DeploymentTarget target)
    {
        var sites = await db.IisSites.AsNoTracking().Where(s => s.ServerId == target.ServerId)
            .Include(s => s.Applications).ThenInclude(a => a.VirtualDirs).ToListAsync();
        var others = new List<string?>();
        foreach (var s in sites)
            foreach (var a in s.Applications)
                foreach (var v in a.VirtualDirs)
                {
                    var isOwnRoot = s.IisSiteId == target.IisSiteId && string.Equals(NormApp(a.Path), NormApp(target.AppPath), StringComparison.OrdinalIgnoreCase) && v.Path == "/";
                    if (!isOwnRoot) others.Add(v.PhysicalPath);
                }
        return NestedFolders.RelativeInside(target.PhysicalPath, others);
    }

    private static string NormApp(string? p) { var v = string.IsNullOrWhiteSpace(p) ? "/" : p.Trim().Replace('\\', '/'); return v.Length > 1 ? v.TrimEnd('/') : v; }
}
