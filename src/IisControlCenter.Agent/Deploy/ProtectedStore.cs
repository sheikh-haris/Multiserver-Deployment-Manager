using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IisControlCenter.Contracts.Deployments;

namespace IisControlCenter.Agent.Deploy;

/// <summary>
/// The agent remembers every protected path it was ever told about for a folder and always applies the UNION.
/// So a compromised or mistaken portal can add protection but can never remove it: shortening a list needs
/// someone to edit the file on this server.
/// </summary>
public sealed class ProtectedStore(AgentDirs dirs)
{
    private sealed record Stored(List<string> DataFolders, List<string> ConfigPatterns);
    private readonly Lock _gate = new();

    public ProtectedPaths Merge(string targetPath, IEnumerable<string> data, IEnumerable<string> config)
    {
        lock (_gate)
        {
            var file = FileFor(targetPath);
            var stored = new Stored([], []);
            if (File.Exists(file))
            {
                try { stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file)) ?? stored; } catch (JsonException) { /* start again, the portal will resend */ }
            }
            var d = stored.DataFolders.Concat(data).Select(Clean).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var c = stored.ConfigPatterns.Concat(config).Select(Clean).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(file, JsonSerializer.Serialize(new Stored(d, c)));
            return new ProtectedPaths(c, d);
        }
    }

    public ProtectedPaths Load(string targetPath)
    {
        lock (_gate)
        {
            var file = FileFor(targetPath);
            if (!File.Exists(file)) return new ProtectedPaths([], []);
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file)) ?? new Stored([], []);
            return new ProtectedPaths(s.ConfigPatterns, s.DataFolders);
        }
    }

    private static string Clean(string s) => (s ?? "").Trim().Replace('\\', '/').Trim('/');

    private string FileFor(string targetPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetPath.Trim().TrimEnd('\\', '/').ToLowerInvariant())))[..24];
        return Path.Combine(dirs.Protected, key + ".json");
    }
}
