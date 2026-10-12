namespace IisControlCenter.Agent.Deploy;

/// <summary>Where the agent keeps its working files. Locked to administrators by the installer.</summary>
public sealed class AgentDirs
{
    public string Root { get; }
    public string Backups => Path.Combine(Root, "backups");
    public string Runs => Path.Combine(Root, "runs");
    public string Staging => Path.Combine(Root, "staging");
    public string Protected => Path.Combine(Root, "protected");

    public AgentDirs(string root)
    {
        Root = root;
        foreach (var d in new[] { Backups, Runs, Staging, Protected }) Directory.CreateDirectory(d);
    }
}
