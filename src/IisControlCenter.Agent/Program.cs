using IisControlCenter.Agent;
using IisControlCenter.Agent.Config;
using IisControlCenter.Agent.Iis;
using IisControlCenter.Agent.Registration;
using IisControlCenter.Agent.Security;

if (args.Length > 0 && args[0] == "register")
    return await RegisterCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0] == "version")
{
    Console.WriteLine(AgentInfo.Version);
    return 0;
}

if (args.Length > 0 && args[0] == "discover")
{
    // Prints exactly what the agent would send for discovery. Read only. Useful to show security reviewers.
    IIisInspector inspector = OperatingSystem.IsWindows() ? new IisInspector() : new SampleInspector();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspector.Collect(Guid.Empty), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "IisControlCenterAgent");

var config = AgentConfig.Load();                       // fails fast on a bad or missing local config
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CommandPolicy>();

// The sample inspector exists only for development on non-Windows machines.
if (OperatingSystem.IsWindows()) builder.Services.AddSingleton<IIisInspector, IisInspector>();
else builder.Services.AddSingleton<IIisInspector, SampleInspector>();

// ---- deployment services ----
var dataDir = config.SourcePath is null ? AgentConfig.DefaultDirectory : Path.GetDirectoryName(config.SourcePath)!;
builder.Services.AddSingleton(new IisControlCenter.Agent.Deploy.AgentDirs(dataDir));
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.ProtectedStore>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.BackupStore>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.RunJournal>();
builder.Services.AddSingleton(new IisControlCenter.Agent.Deploy.ExecutorOptions
{
    // The agent's LOCAL allow-list. The portal cannot widen it.
    IsPathAllowed = p => PathGuard.IsUnderAnyRoot(p, config.AllowedRoots),
});
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IisControlCenter.Agent.Iis.ISiteResolver, IisControlCenter.Agent.Iis.IisSiteResolver>();
    builder.Services.AddSingleton<IisControlCenter.Agent.Iis.IPoolController, IisControlCenter.Agent.Iis.IisPoolController>();
}
else
{
    builder.Services.AddSingleton<IisControlCenter.Agent.Iis.ISiteResolver, IisControlCenter.Agent.Iis.SampleSiteResolver>();
    builder.Services.AddSingleton<IisControlCenter.Agent.Iis.IPoolController, IisControlCenter.Agent.Iis.SamplePoolController>();
}
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.IHealthChecker, IisControlCenter.Agent.Deploy.LocalHealthChecker>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.IAgentAntivirus, IisControlCenter.Agent.Deploy.DefenderAgentAntivirus>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.IPackageSource>(sp =>
    new IisControlCenter.Agent.Deploy.HttpPackageSource(config, () => AgentCertificates.Load(config.CertThumbprint), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<IisControlCenter.Agent.Deploy.HttpPackageSource>>()));
builder.Services.AddSingleton(sp => new IisControlCenter.Agent.Deploy.HttpPlanSource(config, () => AgentCertificates.Load(config.CertThumbprint)));
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.IRunPlanSource>(sp => sp.GetRequiredService<IisControlCenter.Agent.Deploy.HttpPlanSource>());
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.IManifestInputSource>(sp => sp.GetRequiredService<IisControlCenter.Agent.Deploy.HttpPlanSource>());
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.TargetValidator>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.FolderInspector>();
builder.Services.AddSingleton<IisControlCenter.Agent.Deploy.DeploymentExecutor>();

builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;
