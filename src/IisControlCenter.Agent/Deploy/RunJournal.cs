using System.Text.Json;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Deploy;

public sealed class JournalEntry
{
    public Guid RunId { get; set; }
    public string Kind { get; set; } = "Deploy";           // Deploy or Rollback
    public string Phase { get; set; } = "Started";
    public string TargetPath { get; set; } = "";
    public long IisSiteId { get; set; }
    public string AppPath { get; set; } = "/";
    public string? BackupId { get; set; }
    public List<string> DataFolders { get; set; } = [];
    public List<string> ConfigPatterns { get; set; } = [];
    public string HealthPath { get; set; } = "/";
    public int ExpectedHealthStatus { get; set; } = 200;
    public bool WeCreatedOffline { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// Written to disk before each risky phase. If the agent or server dies half-way, the next start finds the journal,
/// restores the backup and reports the result. Finished runs keep a "completed" file until the portal confirms it got the result.
/// </summary>
public sealed class RunJournal(AgentDirs dirs)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string JournalFile(Guid id) => Path.Combine(dirs.Runs, id.ToString("N") + ".journal.json");
    private string CompletedFile(Guid id) => Path.Combine(dirs.Runs, id.ToString("N") + ".completed.json");

    public void Save(JournalEntry e) => AtomicWrite(JournalFile(e.RunId), JsonSerializer.Serialize(e, Json));
    public JournalEntry? Load(Guid id) => File.Exists(JournalFile(id)) ? JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(JournalFile(id))) : null;
    public void Delete(Guid id) { if (File.Exists(JournalFile(id))) File.Delete(JournalFile(id)); }

    public IEnumerable<JournalEntry> Unfinished() =>
        Directory.EnumerateFiles(dirs.Runs, "*.journal.json")
            .Select(f => { try { return JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(f)); } catch (JsonException) { return null; } })
            .Where(e => e is not null && !File.Exists(CompletedFile(e.RunId)))!;

    public void SaveCompleted(RunCompleted c) => AtomicWrite(CompletedFile(c.RunId), JsonSerializer.Serialize(c, Json));
    public RunCompleted? LoadCompleted(Guid id) => File.Exists(CompletedFile(id)) ? JsonSerializer.Deserialize<RunCompleted>(File.ReadAllText(CompletedFile(id))) : null;
    public IEnumerable<RunCompleted> AllCompleted() =>
        Directory.EnumerateFiles(dirs.Runs, "*.completed.json").Select(f => JsonSerializer.Deserialize<RunCompleted>(File.ReadAllText(f))!).Where(c => c is not null);

    /// <summary>The portal confirmed it has the result. Journal files are kept for a while for troubleshooting, then removed.</summary>
    public void Acknowledge(Guid id)
    {
        if (File.Exists(CompletedFile(id))) File.Delete(CompletedFile(id));
        Delete(id);
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
