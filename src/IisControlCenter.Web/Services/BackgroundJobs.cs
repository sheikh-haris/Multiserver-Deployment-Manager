using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

/// <summary>Marks agents Offline when heartbeats stop (default: 90 seconds).</summary>
public sealed class AgentMonitor(IServiceScopeFactory scopes, ILogger<AgentMonitor> log) : BackgroundService
{
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<ServerService>().MarkStaleOfflineAsync(OfflineAfter);
                if (n > 0) log.LogInformation("{Count} agent(s) marked offline", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Agent monitor failed"); }
        }
    }
}

/// <summary>Hangfire job: ask every online approved agent for a fresh discovery.</summary>
public sealed class DiscoveryJob(IDbContextFactory<AppDbContext> dbf, AgentCommandService commands, ILogger<DiscoveryJob> log)
{
    public async Task RunAll()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.EnvironmentName != null && s.AgentStatus == AgentStatus.Online).ToListAsync();
        foreach (var s in servers)
        {
            var r = await commands.SendDiscoveryAsync(s);
            if (!r.Ok) log.LogWarning("Discovery for {Server} not sent: {Error}", s.Name, r.Error);
        }
    }
}
