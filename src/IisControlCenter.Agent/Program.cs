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

builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
return 0;
