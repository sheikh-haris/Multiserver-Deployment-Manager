using IisControlCenter.Core.Audit;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record AuditRecord(
    string Action,
    string? TargetType = null,
    string? TargetId = null,
    Guid? ServerId = null,
    string? EnvironmentName = null,
    string? Before = null,
    string? After = null,
    string Result = "Success",
    string? Detail = null);

/// <summary>Append-only writer. Single writer lock keeps the hash chain consistent on one portal node.</summary>
public sealed class AuditService(IDbContextFactory<AppDbContext> dbf, TimeProvider clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public const string SystemUserId = "system";

    public Task WriteAsync(Actor actor, AuditRecord r) =>
        WriteAsync(actor.UserId, actor.UserName, actor.Ip, r);

    public Task WriteSystemAsync(AuditRecord r, string? ip = null) =>
        WriteAsync(SystemUserId, "System", ip, r);

    public async Task WriteAsync(string userId, string userName, string? ip, AuditRecord r)
    {
        await Gate.WaitAsync();
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            var prev = await db.AuditLog.OrderByDescending(x => x.Id).Select(x => x.Hash).FirstOrDefaultAsync() ?? "";
            var e = new AuditEntry
            {
                Timestamp = clock.GetUtcNow(),
                UserId = userId,
                UserName = userName,
                Action = r.Action,
                TargetType = r.TargetType,
                TargetId = r.TargetId,
                ServerId = r.ServerId,
                EnvironmentName = r.EnvironmentName,
                Before = Trim(r.Before),
                After = Trim(r.After),
                Result = r.Result,
                Detail = Trim(r.Detail),
                ClientIp = ip,
                PrevHash = prev,
            };
            e.Hash = AuditHasher.Compute(e);
            db.AuditLog.Add(e);
            await db.SaveChangesAsync();
        }
        finally { Gate.Release(); }
    }

    private static string? Trim(string? s) => s is { Length: > 8000 } ? s[..8000] + "…" : s;
}
