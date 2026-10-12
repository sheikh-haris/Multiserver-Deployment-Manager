using System.Security.Cryptography;
using System.Text;
using IisControlCenter.Core.Domain;

namespace IisControlCenter.Core.Audit;

/// <summary>
/// Each audit row stores the hash of the previous row, so removing or editing a row
/// (for example directly in SQL) breaks the chain and can be detected.
/// </summary>
public static class AuditHasher
{
    public static string Compute(AuditEntry e)
    {
        var canonical = string.Join('\u001f', new[]
        {
            e.PrevHash, e.Timestamp.UtcTicks.ToString(), e.UserId, e.UserName, e.Action,
            e.TargetType, e.TargetId, e.ServerId?.ToString(), e.ProjectId?.ToString(), e.EnvironmentName,
            e.Before, e.After, e.Result, e.Detail, e.CorrelationId, e.ClientIp,
        }.Select(s => s ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Returns the id of the first broken row, or null when the chain is intact. Rows must be ordered by Id.</summary>
    public static long? FindBreak(IEnumerable<AuditEntry> ordered)
    {
        var prev = "";
        foreach (var e in ordered)
        {
            if (e.PrevHash != prev || e.Hash != Compute(e)) return e.Id;
            prev = e.Hash;
        }
        return null;
    }
}
