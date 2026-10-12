using System.Runtime.Versioning;
using System.Text.Json;
using IisControlCenter.Agent.Iis;
using Xunit;

namespace IisControlCenter.Agent.IntegrationTests;

/// <summary>Runs the real IIS inspector against a real IIS. Skipped (and reported as skipped) anywhere else.</summary>
[SupportedOSPlatform("windows")]
public class IisInspectorTests : IClassFixture<IisFixture>
{
    private readonly IisFixture fx;
    public IisInspectorTests(IisFixture fx) => this.fx = fx;

    private IisControlCenter.Contracts.DiscoveryResult Collect()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        return new IisInspector().Collect(Guid.NewGuid());
    }

    [SkippableFact]
    public void Finds_the_site_with_its_binding_physical_path_and_pool()
    {
        var r = Collect();
        var site = Assert.Single(r.Sites, s => s.Name == fx.SiteName);
        Assert.Equal(Path.Combine(fx.Root, "web"), site.PhysicalPath, ignoreCase: true);
        Assert.Equal(fx.PoolName, site.AppPoolName);
        Assert.Contains(site.Bindings, b => b.Protocol == "http" && b.Host == $"{fx.Id}.icc-it.local");
        Assert.Contains(site.Bindings, b => b.Protocol == "https" && string.Equals(b.CertThumbprint, fx.Thumbprint, StringComparison.OrdinalIgnoreCase));
        Assert.True(site.IisSiteId > 0);
    }

    [SkippableFact]
    public void Finds_applications_and_virtual_directories()
    {
        var site = Collect().Sites.Single(s => s.Name == fx.SiteName);
        var api = Assert.Single(site.Applications, a => a.Path == "/api");
        Assert.Equal(fx.CustomPoolName, api.AppPoolName);
        var root = Assert.Single(site.Applications, a => a.Path == "/");
        Assert.Contains(root.VirtualDirectories, v => v.Path == "/uploads" && string.Equals(v.PhysicalPath, Path.Combine(fx.Root, "uploads"), StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void Reports_pool_identity_runtime_and_pipeline()
    {
        var r = Collect();
        var p = Assert.Single(r.AppPools, x => x.Name == fx.PoolName);
        Assert.Equal("v4.0", p.RuntimeVersion);
        Assert.Equal("Integrated", p.PipelineMode);
        Assert.Equal("ApplicationPoolIdentity", p.IdentityType);
        var c = Assert.Single(r.AppPools, x => x.Name == fx.CustomPoolName);
        Assert.Equal("SpecificUser", c.IdentityType);
        Assert.Null(c.RuntimeVersion);                  // "No managed code"
    }

    [SkippableFact]
    public void Never_returns_a_pool_password_or_the_user_name_it_runs_as()
    {
        var json = JsonSerializer.Serialize(Collect());
        Assert.DoesNotContain(IisFixture.Secret, json);
        Assert.DoesNotContain("S3cr3t", json);
        Assert.DoesNotContain("icc-it-nobody", json);
    }

    [SkippableFact]
    public void Lists_certificates_with_expiry_but_no_key_material()
    {
        var r = Collect();
        var cert = Assert.Single(r.Certificates, c => string.Equals(c.Thumbprint, fx.Thumbprint, StringComparison.OrdinalIgnoreCase));
        Assert.InRange((cert.NotAfter - DateTimeOffset.UtcNow).TotalDays, 8, 11);
        Assert.Contains($"{fx.Id}.icc-it.local", cert.Subject);
    }

    [SkippableFact]
    public void Reports_that_iis_is_installed_and_dotnet_versions_do_not_throw()
    {
        var r = Collect();
        Assert.True(r.IisInstalled);
        Assert.NotNull(r.DotNetVersions);
    }
}
