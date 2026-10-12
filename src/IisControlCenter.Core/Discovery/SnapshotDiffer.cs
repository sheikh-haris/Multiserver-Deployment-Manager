using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Discovery;

public sealed record DriftItem(string ObjectType, string ObjectRef, DriftKind Kind, string? Before, string? After);

/// <summary>
/// Compares two discovery snapshots of the same server. Everything in Phase 2 is read-only,
/// so any difference was made outside the portal (for example in IIS Manager).
/// Site state (started/stopped) is ignored on purpose: it changes all the time and is not configuration.
/// </summary>
public static class SnapshotDiffer
{
    public static List<DriftItem> Diff(
        IReadOnlyCollection<IisSite> oldSites, IReadOnlyCollection<IisSite> newSites,
        IReadOnlyCollection<IisAppPool> oldPools, IReadOnlyCollection<IisAppPool> newPools,
        IReadOnlyCollection<IisCertificate> oldCerts, IReadOnlyCollection<IisCertificate> newCerts)
    {
        var d = new List<DriftItem>();

        var os = oldSites.ToDictionary(s => s.IisSiteId); var ns = newSites.ToDictionary(s => s.IisSiteId);
        foreach (var s in newSites.Where(s => !os.ContainsKey(s.IisSiteId)))
            d.Add(new("Site", s.Name, DriftKind.Created, null, Describe(s)));
        foreach (var s in oldSites.Where(s => !ns.ContainsKey(s.IisSiteId)))
            d.Add(new("Site", s.Name, DriftKind.Deleted, Describe(s), null));
        foreach (var n in newSites.Where(s => os.ContainsKey(s.IisSiteId)))
        {
            var o = os[n.IisSiteId];
            var changes = new List<string>();
            if (o.Name != n.Name) changes.Add($"name '{o.Name}' -> '{n.Name}'");
            if (!SamePath(o.PhysicalPath, n.PhysicalPath)) changes.Add($"folder '{o.PhysicalPath}' -> '{n.PhysicalPath}'");
            if (!Same(o.AppPoolName, n.AppPoolName)) changes.Add($"app pool '{o.AppPoolName}' -> '{n.AppPoolName}'");
            var ob = Bindings(o); var nb = Bindings(n);
            if (!ob.SetEquals(nb)) changes.Add($"bindings [{string.Join(", ", ob.OrderBy(x => x))}] -> [{string.Join(", ", nb.OrderBy(x => x))}]");
            var oa = Apps(o); var na = Apps(n);
            if (!oa.SetEquals(na)) changes.Add($"applications [{string.Join(", ", oa.OrderBy(x => x))}] -> [{string.Join(", ", na.OrderBy(x => x))}]");
            if (changes.Count > 0) d.Add(new("Site", n.Name, DriftKind.Changed, Describe(o), string.Join("; ", changes)));
        }

        var op = oldPools.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase); var np = newPools.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var p in newPools.Where(p => !op.ContainsKey(p.Name))) d.Add(new("AppPool", p.Name, DriftKind.Created, null, Describe(p)));
        foreach (var p in oldPools.Where(p => !np.ContainsKey(p.Name))) d.Add(new("AppPool", p.Name, DriftKind.Deleted, Describe(p), null));
        foreach (var n in newPools.Where(p => op.ContainsKey(p.Name)))
        {
            var o = op[n.Name];
            var changes = new List<string>();
            if (!Same(o.IdentityType, n.IdentityType)) changes.Add($"identity {o.IdentityType} -> {n.IdentityType}");
            if (!Same(o.RuntimeVersion ?? "", n.RuntimeVersion ?? "")) changes.Add($"runtime '{o.RuntimeVersion}' -> '{n.RuntimeVersion}'");
            if (!Same(o.PipelineMode, n.PipelineMode)) changes.Add($"pipeline {o.PipelineMode} -> {n.PipelineMode}");
            if (changes.Count > 0) d.Add(new("AppPool", n.Name, DriftKind.Changed, Describe(o), string.Join("; ", changes)));
        }

        var oc = oldCerts.Select(c => c.Thumbprint.ToUpperInvariant()).ToHashSet(); var nc = newCerts.Select(c => c.Thumbprint.ToUpperInvariant()).ToHashSet();
        foreach (var c in newCerts.Where(c => !oc.Contains(c.Thumbprint.ToUpperInvariant()))) d.Add(new("Certificate", c.Subject, DriftKind.Created, null, c.Thumbprint));
        foreach (var c in oldCerts.Where(c => !nc.Contains(c.Thumbprint.ToUpperInvariant()))) d.Add(new("Certificate", c.Subject, DriftKind.Deleted, c.Thumbprint, null));

        return d;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string? a, string? b) => Targets.LinkRules.NormalizePath(a) == Targets.LinkRules.NormalizePath(b);
    private static HashSet<string> Bindings(IisSite s) =>
        s.Bindings.Select(b => $"{b.Protocol}://{b.Host}:{b.Port}").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> Apps(IisSite s) =>
        s.Applications.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Describe(IisSite s) => $"folder={s.PhysicalPath}; pool={s.AppPoolName}";
    private static string Describe(IisAppPool p) => $"identity={p.IdentityType}; runtime={p.RuntimeVersion}; pipeline={p.PipelineMode}";
}
