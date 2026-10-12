using System.Text.Json;
using System.Text.Json.Serialization;

namespace IisControlCenter.Agent.Config;

/// <summary>
/// The agent's LOCAL config. It is the source of truth for what this agent may do.
/// It is written by the installer on the server and is never changed by the portal.
/// </summary>
public sealed class AgentConfig
{
    public string PortalUrl { get; set; } = "";
    public string Environment { get; set; } = "";
    public List<string> AllowedRoots { get; set; } = [];
    public bool AllowManaged { get; set; }
    public Guid ServerId { get; set; }
    public string CertThumbprint { get; set; } = "";

    public static readonly string[] KnownEnvironments = ["Dev", "Staging", "Prod"];

    public static string DefaultDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "IisControlCenter");

    public static string DefaultPath =>
        System.Environment.GetEnvironmentVariable("ICC_AGENT_CONFIG") ?? Path.Combine(DefaultDirectory, "agent.config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AgentConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json)
                  ?? throw new InvalidOperationException("Config file is empty.");
        var problems = cfg.Validate();
        if (problems.Count > 0) throw new InvalidOperationException("Invalid agent config: " + string.Join("; ", problems));
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public List<string> Validate()
    {
        var p = new List<string>();
        if (!Uri.TryCreate(PortalUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) p.Add("PortalUrl must be an https URL");
        if (!KnownEnvironments.Contains(Environment, StringComparer.OrdinalIgnoreCase)) p.Add("Environment must be Dev, Staging or Prod");
        if (AllowedRoots.Count == 0) p.Add("At least one allowed root is required");
        foreach (var r in AllowedRoots)
        {
            var e = Security.PathGuard.ValidateRoot(r);
            if (e is not null) p.Add($"Root '{r}': {e}");
        }
        return p;
    }
}
