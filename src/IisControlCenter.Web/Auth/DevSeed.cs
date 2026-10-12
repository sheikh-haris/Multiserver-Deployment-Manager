using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Auth;

/// <summary>Development-only sign-in personas and their roles. Never compiled into a path that runs outside Development.</summary>
public static class DevSeed
{
    public static readonly (string Key, string Oid, string Name)[] Personas =
    [
        ("admin", "dev-admin", "Dev Admin (Platform Admin)"),
        ("operator", "dev-operator", "Dev Operator (Server Operator, Dev only)"),
        ("auditor", "dev-auditor", "Dev Auditor"),
        ("nobody", "dev-nobody", "Dev User (no roles)"),
        // Project roles for these are granted per project by the automated tests (/dev/e2e/grant) or by an admin on the project page.
        ("deployer", "dev-deployer", "Dev Deployer (project role)"),
        ("approver", "dev-approver", "Dev Approver (project role)"),
        ("approver2", "dev-approver2", "Dev Approver 2 (project role)"),
    ];

    public static async Task RunAsync(AppDbContext db)
    {
        async Task Ensure(string oid, string name, Role role, string? env)
        {
            if (await db.RoleAssignments.AnyAsync(r => r.PrincipalId == oid && r.RevokedAt == null)) return;
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = oid, PrincipalName = name, Scope = RoleScope.Platform,
                Role = role, EnvironmentName = env, GrantedBy = "dev-seed", GrantedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            });
        }
        await Ensure("dev-admin", "Dev Admin", Role.PlatformAdmin, null);
        await Ensure("dev-operator", "Dev Operator", Role.ServerOperator, "Dev");
        await Ensure("dev-auditor", "Dev Auditor", Role.Auditor, null);
        await db.SaveChangesAsync();
    }
}
