using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed class RoleService(IDbContextFactory<AppDbContext> dbf, AuditService audit, TimeProvider clock)
{
    public async Task<List<RoleAssignment>> ListPlatformAsync(Actor actor)
    {
        if (!actor.Can(Permission.ManageRoles)) throw new ForbiddenException("You do not have permission to manage roles.");
        await using var db = await dbf.CreateDbContextAsync();
        return await db.RoleAssignments.AsNoTracking()
            .Where(r => r.Scope == RoleScope.Platform && r.RevokedAt == null)
            .OrderBy(r => r.PrincipalName).ToListAsync();
    }

    public async Task<OpResult> GrantAsync(Actor actor, PrincipalType type, string principalId, string principalName,
        Role role, string? environment, DateTimeOffset? expiresAt)
    {
        await RequireAsync(actor, "Role.Grant", principalId);
        if (!RolePermissions.PlatformRoles.Contains(role)) return OpResult.Fail("Not a platform role.");
        principalId = (principalId ?? "").Trim();
        if (principalId.Length == 0) return OpResult.Fail("Enter the group or user object id.");
        if (type == PrincipalType.User && expiresAt is null) return OpResult.Fail("Individual users need an expiry date. Use a group for permanent access.");
        if (expiresAt is not null && expiresAt <= clock.GetUtcNow()) return OpResult.Fail("Expiry must be in the future.");
        if (!RolePermissions.IsEnvironmentScoped(role)) environment = null;
        if (RolePermissions.IsEnvironmentScoped(role) && string.IsNullOrEmpty(environment))
            return OpResult.Fail("Choose an environment, or 'All environments' explicitly.");
        if (environment == AllEnvironments) environment = null;

        await using var db = await dbf.CreateDbContextAsync();
        if (environment is not null && !await db.Environments.AnyAsync(e => e.Name == environment))
            return OpResult.Fail("Unknown environment.");

        var a = new RoleAssignment
        {
            Id = Guid.NewGuid(), PrincipalType = type, PrincipalId = principalId, PrincipalName = string.IsNullOrWhiteSpace(principalName) ? principalId : principalName.Trim(),
            Scope = RoleScope.Platform, Role = role, EnvironmentName = environment, GrantedBy = actor.UserName,
            GrantedAt = clock.GetUtcNow(), ExpiresAt = expiresAt,
        };
        db.RoleAssignments.Add(a);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Role.Grant", "RoleAssignment", a.Id.ToString(), EnvironmentName: environment,
            After: $"{type} {a.PrincipalName} ({principalId}) -> {role}; env={environment ?? "all"}; expires={expiresAt:O}"));
        return OpResult.Success();
    }

    public const string AllEnvironments = "*";

    public async Task<OpResult> RevokeAsync(Actor actor, Guid id)
    {
        await RequireAsync(actor, "Role.Revoke", id.ToString());
        await using var db = await dbf.CreateDbContextAsync();
        var a = await db.RoleAssignments.FirstOrDefaultAsync(r => r.Id == id && r.RevokedAt == null);
        if (a is null) return OpResult.Fail("Assignment not found.");
        // Never remove the last Platform Admin assignment through the UI (bootstrap admins from config are the safety net).
        a.RevokedAt = clock.GetUtcNow(); a.RevokedBy = actor.UserName;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Role.Revoke", "RoleAssignment", id.ToString(), EnvironmentName: a.EnvironmentName,
            Before: $"{a.PrincipalType} {a.PrincipalName} ({a.PrincipalId}) -> {a.Role}; env={a.EnvironmentName ?? "all"}"));
        return OpResult.Success();
    }

    private async Task RequireAsync(Actor actor, string action, string target)
    {
        if (actor.Can(Permission.ManageRoles)) return;
        await audit.WriteAsync(actor, new AuditRecord(action, "RoleAssignment", target, Result: "Denied"));
        throw new ForbiddenException("You do not have permission to manage roles.");
    }
}
