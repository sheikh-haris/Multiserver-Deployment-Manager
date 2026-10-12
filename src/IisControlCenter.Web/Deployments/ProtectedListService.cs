using System.Security.Cryptography;
using System.Text;
using IisControlCenter.Contracts.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Deployments;

/// <summary>
/// Edits a component's protected lists and records the Platform Admin's confirmation that the data folders are complete.
/// Full Replace deletes files, so it stays blocked until that confirmation exists for the CURRENT list. Any change voids it.
/// </summary>
public sealed class ProtectedListService(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    public static string HashOf(IEnumerable<ProtectedPath> list)
    {
        var text = string.Join('\n', list.Where(p => p.Kind == ProtectedPathKind.DataFolder)
            .Select(p => p.Path.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string? ValidatePattern(string input, ProtectedPathKind kind, out string normalized)
    {
        normalized = (input ?? "").Trim();
        if (normalized.Length == 0) return "Enter a path.";
        if (normalized.Length > 300) return "The path is too long.";
        if (normalized.Contains("..") || normalized.Contains(':') || normalized.StartsWith('/') || normalized.StartsWith('\\')) return "Use a path relative to the site folder, without '..' or a drive letter.";
        if (normalized.IndexOfAny(['<', '>', '"', '|', '\0']) >= 0) return "The path has characters that are not allowed.";
        normalized = normalized.Replace('/', '\\');
        if (kind == ProtectedPathKind.DataFolder)
        {
            if (normalized.Contains('*') && !normalized.StartsWith("**\\")) return "Data folders may use wildcards only as a leading '**\\' (any depth).";
            if (!normalized.EndsWith('\\')) normalized += "\\";
        }
        return null;
    }

    public async Task<OpResult> AddAsync(Actor actor, Guid componentId, string path, ProtectedPathKind kind)
    {
        Require(actor);
        var err = ValidatePattern(path, kind, out var norm);
        if (err is not null) return OpResult.Fail(err);
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.Include(x => x.ProtectedPaths).FirstOrDefaultAsync(x => x.Id == componentId);
        if (c is null) return OpResult.Fail("Component not found.");
        if (c.ProtectedPaths.Any(p => p.Kind == kind && string.Equals(p.Path, norm, StringComparison.OrdinalIgnoreCase))) return OpResult.Fail("That path is already in the list.");
        var before = HashOf(c.ProtectedPaths);
        var added = new ProtectedPath { Id = Guid.NewGuid(), ComponentId = componentId, Path = norm, Kind = kind };
        db.ProtectedPaths.Add(added);       // explicit: adding through the navigation of a tracked entity with a preset key is treated as an update
        await VoidIfChanged(db, c.Id, HashOf(c.ProtectedPaths.Append(added)), before);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("ProtectedPath.Add", "Component", componentId.ToString(), After: $"{kind}: {norm}"));
        return OpResult.Success();
    }

    public async Task<OpResult> RemoveAsync(Actor actor, Guid componentId, Guid pathId)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.Include(x => x.ProtectedPaths).FirstOrDefaultAsync(x => x.Id == componentId);
        var p = c?.ProtectedPaths.FirstOrDefault(x => x.Id == pathId);
        if (c is null || p is null) return OpResult.Fail("Path not found.");
        if (p.IsDefault) return OpResult.Fail("The default protected paths cannot be removed.");
        var before = HashOf(c.ProtectedPaths);
        db.ProtectedPaths.Remove(p);
        await VoidIfChanged(db, c.Id, HashOf(c.ProtectedPaths.Where(x => x.Id != p.Id)), before);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("ProtectedPath.Remove", "Component", componentId.ToString(), Before: $"{p.Kind}: {p.Path}"));
        return OpResult.Success();
    }

    private static async Task VoidIfChanged(AppDbContext db, Guid componentId, string afterHash, string beforeHash)
    {
        if (afterHash == beforeHash) return;
        var conf = await db.ProtectedListConfirmations.FirstOrDefaultAsync(x => x.ComponentId == componentId);
        if (conf is not null) db.ProtectedListConfirmations.Remove(conf);
    }

    public async Task<OpResult> ConfirmAsync(Actor actor, Guid componentId)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.Include(x => x.ProtectedPaths).FirstOrDefaultAsync(x => x.Id == componentId);
        if (c is null) return OpResult.Fail("Component not found.");
        var conf = await db.ProtectedListConfirmations.FirstOrDefaultAsync(x => x.ComponentId == componentId);
        if (conf is null) db.ProtectedListConfirmations.Add(conf = new ProtectedListConfirmation { ComponentId = componentId });
        conf.ConfirmedBy = actor.UserName; conf.ConfirmedAt = clock.GetUtcNow(); conf.ListHash = HashOf(c.ProtectedPaths);
        await db.SaveChangesAsync();
        var folders = string.Join(", ", c.ProtectedPaths.Where(p => p.Kind == ProtectedPathKind.DataFolder).Select(p => p.Path));
        await audit.WriteAsync(actor, new AuditRecord("ProtectedList.Confirm", "Component", componentId.ToString(), After: $"data folders: {(folders.Length == 0 ? "(none)" : folders)}"));
        return OpResult.Success();
    }

    public async Task<ProtectedListConfirmation?> GetConfirmationAsync(Guid componentId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var c = await db.Components.AsNoTracking().Include(x => x.ProtectedPaths).FirstOrDefaultAsync(x => x.Id == componentId);
        var conf = await db.ProtectedListConfirmations.AsNoTracking().FirstOrDefaultAsync(x => x.ComponentId == componentId);
        return c is not null && conf is not null && conf.ListHash == HashOf(c.ProtectedPaths) ? conf : null;
    }

    private static void Require(Actor a)
    {
        if (!a.Can(Permission.ManageProjects)) throw new ForbiddenException("Only a Platform Admin can change the protected lists.");
    }
}
