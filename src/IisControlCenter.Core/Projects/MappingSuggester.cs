using System.Text.RegularExpressions;

namespace IisControlCenter.Core.Projects;

public enum Confidence { None, Low, Medium, High }

public sealed record MappingSuggestion(string? ProjectName, string? ComponentName, Confidence Confidence, string Reason)
{
    public static readonly MappingSuggestion None = new(null, null, Confidence.None, "No suggestion");
}

public sealed record KnownProject(string Name, IReadOnlyList<string> Components);

/// <summary>
/// Suggests a project and component from the site name, host names and folder. It only suggests.
/// A person confirms every mapping, and nothing is ever saved from a suggestion alone.
/// </summary>
public static class MappingSuggester
{
    private static readonly string[] EnvWords = ["dev", "development", "stg", "stage", "staging", "uat", "test", "prod", "prd", "production", "live"];

    public static string Norm(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    public static MappingSuggestion Suggest(string siteName, IEnumerable<string?> hosts, string? physicalPath, IReadOnlyList<KnownProject> projects)
    {
        // 1. Folder like ...\<project>\<component>
        var segs = (physicalPath ?? "").Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segs.Count >= 3)   // drive + project + component at least
        {
            var comp = segs[^1]; var proj = segs[^2];
            var p = projects.FirstOrDefault(x => Norm(x.Name) == Norm(proj));
            if (p is not null)
            {
                var known = p.Components.FirstOrDefault(c => Norm(c) == Norm(comp));
                return new(p.Name, known ?? comp, known is not null ? Confidence.High : Confidence.Medium,
                    $"Folder '{physicalPath}' looks like <project>\\<component>.");
            }
        }

        // 2. Site name like <project>-<component>-<env>
        var parts = siteName.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var body = EnvWords.Contains(parts[^1].ToLowerInvariant()) ? parts[..^1] : parts;
            if (body.Length >= 2)
            {
                var p = projects.FirstOrDefault(x => Norm(x.Name) == Norm(body[0]));
                var compName = string.Join(" ", body[1..]);
                if (p is not null)
                {
                    var known = p.Components.FirstOrDefault(c => Norm(c) == Norm(compName));
                    return new(p.Name, known ?? compName, known is not null ? Confidence.High : Confidence.Medium,
                        $"Site name '{siteName}' looks like <project>-<component>-<env>.");
                }
            }
        }

        // 3. A project name appears in a host name or the site name.
        foreach (var p in projects.Where(x => Norm(x.Name).Length >= 3))
        {
            var n = Norm(p.Name);
            var hit = hosts.FirstOrDefault(h => Norm(h).Contains(n));
            if (hit is not null) return new(p.Name, null, Confidence.Low, $"Host name '{hit}' contains the project name.");
            if (Norm(siteName).Contains(n)) return new(p.Name, null, Confidence.Low, $"Site name contains the project name.");
        }
        return MappingSuggestion.None;
    }
}
