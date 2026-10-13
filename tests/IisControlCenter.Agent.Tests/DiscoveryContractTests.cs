using System.Reflection;
using System.Text.RegularExpressions;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Contracts;

namespace IisControlCenter.Agent.Tests;

public class DiscoveryContractTests
{
    /// <summary>
    /// The agent must never send app pool passwords, connection strings or file contents.
    /// If someone adds such a field to the discovery messages, this fails.
    /// </summary>
    [Fact]
    public void Discovery_messages_have_no_secret_like_fields()
    {
        var bad = new Regex("pass|pwd|secret|credential|connection|content|token|key(?!word)|private", RegexOptions.IgnoreCase);
        var types = typeof(DiscoveryResult).Assembly.GetTypes()
            .Where(t => t.Name.StartsWith("Discovered") || t == typeof(DiscoveryResult)).ToList();
        Assert.True(types.Count >= 7);
        var offenders = types.SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}")).Where(n => bad.IsMatch(n.Split('.')[1])).ToList();
        Assert.True(offenders.Count == 0, "Secret-like fields: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Sample_inspector_produces_data_that_exercises_the_report_cases()
    {
        var r = new SampleInspector(new SamplePoolAdmin()).Collect(Guid.NewGuid());
        Assert.NotEmpty(r.Sites);
        Assert.Contains(r.AppPools, p => p.IdentityType == "LocalSystem");
        Assert.Contains(r.Certificates, c => c.NotAfter < DateTimeOffset.UtcNow.AddDays(30));
        Assert.Contains(r.Sites.SelectMany(s => s.Applications), a => a.VirtualDirectories.Count > 1);
    }

    [Theory]
    [InlineData(533320, "4.8.1")]
    [InlineData(528040, "4.8")]
    [InlineData(461808, "4.7.2")]
    [InlineData(394802, "4.6.2")]
    [InlineData(378389, "4.5")]
    [InlineData(100, "4.x (release 100)")]
    public void Maps_framework_release_numbers(int release, string name) => Assert.Equal(name, DotNetVersions.FrameworkName(release));
}
