using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Targets;

public enum LinkViolationCode
{
    ServerNotApproved,
    EnvironmentMismatch,
    SiteAlreadyLinked,
    ComponentAlreadyOnServer,
    PoolUsedByOtherComponent,   // can be allowed by a recorded admin exception
    PathOverlap,
    MissingData,
}

public sealed record LinkViolation(LinkViolationCode Code, string Message, bool Exceptionable);

/// <summary>What is already linked on the server, with the owning component and project names for messages.</summary>
public sealed record ExistingLink(
    Guid TargetId, Guid ComponentId, string ComponentName, Guid ProjectId, string ProjectName,
    string EnvironmentName, long IisSiteId, string AppPath, string PhysicalPath, string AppPoolName);

public sealed record LinkCandidate(
    Guid ComponentId, Guid ProjectId, string EnvironmentName, Guid ServerId, string? ServerEnvironment,
    long IisSiteId, string AppPath, string PhysicalPath, string AppPoolName);

public static class LinkRules
{
    public static string NormalizePath(string? p) =>
        (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public static bool PathsOverlap(string? a, string? b)
    {
        var x = NormalizePath(a); var y = NormalizePath(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || x.StartsWith(y + "\\", StringComparison.Ordinal) || y.StartsWith(x + "\\", StringComparison.Ordinal);
    }

    public static string NormalizeAppPath(string? p)
    {
        var v = string.IsNullOrWhiteSpace(p) ? "/" : p.Trim().Replace('\\', '/');
        if (!v.StartsWith('/')) v = "/" + v;
        return v.Length > 1 ? v.TrimEnd('/') : v;
    }

    /// <summary>
    /// Checks every rule that must hold for a new link. The caller decides whether an "exceptionable" violation
    /// may be waived (Platform Admin with a recorded reason). Blocking violations can never be waived.
    /// </summary>
    public static List<LinkViolation> Check(LinkCandidate c, IReadOnlyCollection<ExistingLink> existingOnServer)
    {
        var v = new List<LinkViolation>();
        if (c.ServerEnvironment is null)
            v.Add(new(LinkViolationCode.ServerNotApproved, "The server is not approved yet.", false));
        else if (!string.Equals(c.ServerEnvironment, c.EnvironmentName, StringComparison.OrdinalIgnoreCase))
            v.Add(new(LinkViolationCode.EnvironmentMismatch,
                $"The server belongs to {c.ServerEnvironment}, so it can only hold {c.ServerEnvironment} targets.", false));

        if (string.IsNullOrWhiteSpace(c.PhysicalPath) || string.IsNullOrWhiteSpace(c.AppPoolName))
            v.Add(new(LinkViolationCode.MissingData, "The item has no physical path or no app pool in the last discovery.", false));

        var appPath = NormalizeAppPath(c.AppPath);
        foreach (var e in existingOnServer)
        {
            if (e.IisSiteId == c.IisSiteId && string.Equals(NormalizeAppPath(e.AppPath), appPath, StringComparison.OrdinalIgnoreCase))
                v.Add(new(LinkViolationCode.SiteAlreadyLinked,
                    $"This site/application is already linked to {e.ProjectName} / {e.ComponentName} ({e.EnvironmentName}).", false));

            else if (e.ComponentId == c.ComponentId && string.Equals(e.EnvironmentName, c.EnvironmentName, StringComparison.OrdinalIgnoreCase))
                v.Add(new(LinkViolationCode.ComponentAlreadyOnServer,
                    $"{e.ComponentName} already has a {e.EnvironmentName} target on this server.", false));

            if (!string.IsNullOrEmpty(c.AppPoolName) && string.Equals(e.AppPoolName, c.AppPoolName, StringComparison.OrdinalIgnoreCase)
                && e.ComponentId != c.ComponentId)
                v.Add(new(LinkViolationCode.PoolUsedByOtherComponent,
                    e.ProjectId == c.ProjectId
                        ? $"App pool '{c.AppPoolName}' is already used by component {e.ComponentName}. A pool should belong to one component."
                        : $"App pool '{c.AppPoolName}' is already used by project {e.ProjectName} / {e.ComponentName}. Pools must not be shared across projects.",
                    Exceptionable: true));

            if (!(e.IisSiteId == c.IisSiteId && string.Equals(NormalizeAppPath(e.AppPath), appPath, StringComparison.OrdinalIgnoreCase))
                && PathsOverlap(e.PhysicalPath, c.PhysicalPath))
                v.Add(new(LinkViolationCode.PathOverlap,
                    $"The folder overlaps with the folder of {e.ProjectName} / {e.ComponentName} ({e.PhysicalPath}). " +
                    "A deployment could overwrite or delete the other component's files.", false));
        }
        return v;
    }
}
