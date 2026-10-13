using System.Runtime.Versioning;
using IisControlCenter.Contracts;
using Microsoft.Web.Administration;

namespace IisControlCenter.Agent.Iis;

/// <summary>What the agent itself learns from IIS about one application. The agent never trusts the portal for these.</summary>
public sealed record ResolvedApp(string PhysicalPath, string PoolName, IReadOnlyList<DiscoveredBinding> Bindings)
{
    /// <summary>
    /// Folders of every OTHER application and virtual directory on this server (any site). The executor protects the ones inside the target folder:
    /// an application in a sub-folder is deployed by its own request, never by its parent's.
    /// </summary>
    public IReadOnlyList<string> OtherIisFolders { get; init; } = [];
}

public interface ISiteResolver
{
    ResolvedApp? Resolve(long iisSiteId, string appPath);
}

/// <summary>The only IIS actions a deployment may take, and only on the pool of the target it is deploying.</summary>
public interface IPoolController
{
    /// <summary>Recycles the pool, or starts it if it was stopped. Returns a short description of what happened.</summary>
    string Restart(string poolName);
}

[SupportedOSPlatform("windows")]
public sealed class IisSiteResolver : ISiteResolver
{
    public ResolvedApp? Resolve(long iisSiteId, string appPath)
    {
        using var mgr = new ServerManager();
        var site = mgr.Sites.FirstOrDefault(s => s.Id == iisSiteId);
        if (site is null) return null;
        var path = string.IsNullOrWhiteSpace(appPath) ? "/" : appPath.Replace('\\', '/');
        if (!path.StartsWith('/')) path = "/" + path;
        var app = site.Applications.FirstOrDefault(a => string.Equals(a.Path.TrimEnd('/') is { Length: > 0 } p1 ? p1 : "/", path.TrimEnd('/') is { Length: > 0 } p2 ? p2 : "/", StringComparison.OrdinalIgnoreCase));
        if (app is null) return null;
        var root = app.VirtualDirectories.FirstOrDefault(v => v.Path == "/");
        if (root is null) return null;
        var bindings = site.Bindings.Select(b => new DiscoveredBinding(b.Protocol, b.EndPoint?.Address?.ToString(), b.EndPoint?.Port ?? 0,
            string.IsNullOrEmpty(b.Host) ? null : b.Host, null)).ToList();
        var others = new List<string>();
        foreach (var st in mgr.Sites)
            foreach (var a in st.Applications)
                foreach (var v in a.VirtualDirectories)
                {
                    if (st.Id == site.Id && ReferenceEquals(a, app) && v.Path == "/") continue;          // the target's own root
                    if (st.Id == site.Id && a.Path == app.Path && v.Path == "/") continue;
                    if (!string.IsNullOrWhiteSpace(v.PhysicalPath)) others.Add(Environment.ExpandEnvironmentVariables(v.PhysicalPath));
                }
        return new ResolvedApp(Environment.ExpandEnvironmentVariables(root.PhysicalPath), app.ApplicationPoolName, bindings) { OtherIisFolders = others };
    }
}

[SupportedOSPlatform("windows")]
public sealed class IisPoolController : IPoolController
{
    public string Restart(string poolName)
    {
        using var mgr = new ServerManager();
        var pool = mgr.ApplicationPools[poolName] ?? throw new InvalidOperationException($"App pool '{poolName}' not found.");
        switch (pool.State)
        {
            case ObjectState.Started: pool.Recycle(); return $"Recycled app pool '{poolName}'.";
            case ObjectState.Stopped: pool.Start(); return $"Started app pool '{poolName}' (it was stopped).";
            default: return $"App pool '{poolName}' is {pool.State}; left as it is.";
        }
    }
}
