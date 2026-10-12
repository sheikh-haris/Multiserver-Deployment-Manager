using System.Text;
using IisControlCenter.Core.Audit;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record AuditFilter(string? Search, string? Action, string? Result, DateTimeOffset? From, DateTimeOffset? To);

public sealed class AuditQueries(IDbContextFactory<AppDbContext> dbf, AuditService audit)
{
    private static IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> q, AuditFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var t = f.Search.Trim();
            q = q.Where(a => a.UserName.Contains(t) || a.Action.Contains(t) || (a.TargetId != null && a.TargetId.Contains(t))
                             || (a.Detail != null && a.Detail.Contains(t)) || (a.After != null && a.After.Contains(t)) || (a.Before != null && a.Before.Contains(t)));
        }
        if (!string.IsNullOrWhiteSpace(f.Action)) q = q.Where(a => a.Action.StartsWith(f.Action));
        if (!string.IsNullOrWhiteSpace(f.Result)) q = q.Where(a => a.Result == f.Result);
        if (f.From is not null) q = q.Where(a => a.Timestamp >= f.From);
        if (f.To is not null) q = q.Where(a => a.Timestamp <= f.To);
        return q;
    }

    public async Task<(IReadOnlyList<AuditEntry> Rows, int Total)> ListAsync(Actor actor, AuditFilter f, int page, int pageSize)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var q = Apply(db.AuditLog.AsNoTracking(), f);
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(a => a.Id).Skip(page * pageSize).Take(pageSize).ToListAsync();
        return (rows, total);
    }

    /// <summary>Verifies the hash chain over the whole log (read only).</summary>
    public async Task<long?> VerifyChainAsync(Actor actor)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var all = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        return AuditHasher.FindBreak(all);
    }

    public async Task<string> ExportCsvAsync(Actor actor, AuditFilter f)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await Apply(db.AuditLog.AsNoTracking(), f).OrderByDescending(a => a.Id).Take(50_000).ToListAsync();
        var sb = new StringBuilder("Id,Timestamp,User,Action,TargetType,TargetId,Environment,Result,Detail,Before,After,ClientIp\n");
        foreach (var a in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                a.Id.ToString(), a.Timestamp.ToString("O"), a.UserName, a.Action, a.TargetType, a.TargetId, a.EnvironmentName,
                a.Result, a.Detail, a.Before, a.After, a.ClientIp,
            }.Select(Csv)));
        await audit.WriteAsync(actor, new AuditRecord("Audit.Export", Detail: $"{rows.Count} rows"));
        return sb.ToString();
    }

    /// <summary>Quotes values and neutralises spreadsheet formulas (CSV injection).</summary>
    public static string Csv(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static void Require(Actor a)
    {
        if (!a.Can(Permission.ViewAudit)) throw new ForbiddenException("You do not have permission to view the audit log.");
    }
}
