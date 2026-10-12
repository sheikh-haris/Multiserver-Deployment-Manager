using IisControlCenter.Agent.Config;

namespace IisControlCenter.Agent.Tests;

public class AgentConfigTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "icc-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    private static AgentConfig Valid() => new() { PortalUrl = "https://agents.example.local", Environment = "Dev", AllowedRoots = [@"d:\websites"], ServerId = Guid.NewGuid(), CertThumbprint = "AB" };

    [Fact] public void Valid_config_has_no_problems() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData("http://agents.example.local")]       // must be https
    [InlineData("agents.example.local")]
    [InlineData("")]
    public void Portal_url_must_be_https(string url)
    {
        var c = Valid(); c.PortalUrl = url;
        Assert.NotEmpty(c.Validate());
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("")]
    [InlineData("Production")]
    public void Environment_must_be_known(string env)
    {
        var c = Valid(); c.Environment = env;
        Assert.NotEmpty(c.Validate());
    }

    [Fact]
    public void At_least_one_safe_root_is_required()
    {
        var c = Valid(); c.AllowedRoots = [];
        Assert.NotEmpty(c.Validate());
        c.AllowedRoots = [@"C:\Windows"];
        Assert.NotEmpty(c.Validate());
        c.AllowedRoots = [@"D:\Websites", @"C:\"];
        Assert.NotEmpty(c.Validate());
    }

    [Fact]
    public void Saves_and_loads_the_same_values()
    {
        var path = Path.Combine(dir, "agent.config.json");
        var c = Valid(); c.AllowManaged = true;
        c.Save(path);
        var back = AgentConfig.Load(path);
        Assert.Equal(c.ServerId, back.ServerId);
        Assert.Equal("Dev", back.Environment);
        Assert.True(back.AllowManaged);
        Assert.Equal(c.AllowedRoots, back.AllowedRoots);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_tampered_config_with_a_dangerous_root_is_refused_at_load()
    {
        var path = Path.Combine(dir, "agent.config.json");
        var c = Valid(); c.Save(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace(@"d:\\websites", @"c:\\windows"));
        Assert.Throws<InvalidOperationException>(() => AgentConfig.Load(path));
    }
}
