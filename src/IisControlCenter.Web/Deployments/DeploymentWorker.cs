using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// One timer for everything deployments need in the background: finish uploads, move requests along, send email, tidy up.
/// A timer (not Hangfire) because the work is a few cheap database checks and must also run in tests and when Hangfire is off.
/// </summary>
public sealed class DeploymentWorker(IServiceScopeFactory scopes, ILogger<DeploymentWorker> log) : BackgroundService
{
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                await sp.GetRequiredService<UploadService>().ProcessPendingAsync(ct);
                await sp.GetRequiredService<DeploymentOrchestrator>().TickAsync(ct);
                await sp.GetRequiredService<NotificationService>().SendPendingAsync(ct);
                if (DateTimeOffset.UtcNow - _lastMaintenance > TimeSpan.FromMinutes(10))
                {
                    _lastMaintenance = DateTimeOffset.UtcNow;
                    await sp.GetRequiredService<MaintenanceService>().RunAsync();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Deployment worker tick failed"); }
        }
    }
}

/// <summary>Expires old requests, removes old packages and upload leftovers.</summary>
public sealed class MaintenanceService(
    IDbContextFactory<AppDbContext> dbf, DeploymentService deployments, UploadService uploads, IPackageStore store, AuditService audit, TimeProvider clock)
{
    public async Task RunAsync()
    {
        await deployments.ExpireDueAsync();
        await uploads.CleanupAsync();
        await PurgePackagesAsync();
    }

    /// <summary>Deletes packages past retention unless an open request still needs them. The database row and the audit trail stay.</summary>
    public async Task<int> PurgePackagesAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var now = clock.GetUtcNow();
        var open = new[] { RequestStatus.Submitted, RequestStatus.Approved, RequestStatus.Scheduled, RequestStatus.Running, RequestStatus.NeedsReapproval };
        var inUse = await db.DeploymentRequests.AsNoTracking().Where(r => open.Contains(r.Status)).Select(r => r.PackageId).ToListAsync();
        var old = await db.Packages.Where(p => p.DeletedAt == null && p.ExpiresAt < now && !inUse.Contains(p.Id)).Take(200).ToListAsync();
        foreach (var p in old)
        {
            try { store.Delete(p); } catch (IOException) { continue; }
            p.DeletedAt = now;
        }
        await db.SaveChangesAsync();
        if (old.Count > 0) await audit.WriteSystemAsync(new AuditRecord("Package.Purge", "Package", After: $"{old.Count} package(s) past retention removed"));
        return old.Count;
    }
}
