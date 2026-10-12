using System.Text.Json;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Iis;

/// <summary>Development helper for machines without IIS: sites described in a JSON file named by ICC_SAMPLE_SITES_FILE.</summary>
public sealed record SampleSite(long Id, string Name, string Path, string Pool, int Port, string? Host);

public static class SampleSites
{
    public static IReadOnlyList<SampleSite>? Load()
    {
        var file = Environment.GetEnvironmentVariable("ICC_SAMPLE_SITES_FILE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        return JsonSerializer.Deserialize<List<SampleSite>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}

public sealed class SampleSiteResolver : ISiteResolver
{
    public ResolvedApp? Resolve(long iisSiteId, string appPath)
    {
        var s = SampleSites.Load()?.FirstOrDefault(x => x.Id == iisSiteId);
        if (s is null || (appPath is not "/" and not "")) return null;
        return new ResolvedApp(s.Path, s.Pool, [new DiscoveredBinding("http", "*", s.Port, s.Host, null)]);
    }
}

/// <summary>Development helper: pretends to recycle a pool.</summary>
public sealed class SamplePoolController : IPoolController
{
    public string Restart(string poolName) => $"(sample) would recycle app pool '{poolName}'.";
}
