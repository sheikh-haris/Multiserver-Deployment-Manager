using System.Security.Claims;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Auth;

public sealed class PortalAuthOptions
{
    public const string Section = "Auth";
    /// <summary>"Entra" (production) or "Dev" (Development environment only).</summary>
    public string Mode { get; set; } = "";
    /// <summary>Object ids or user names that are Platform Admin even with an empty database. Prevents lock-out on first start.</summary>
    public string[] BootstrapAdmins { get; set; } = [];
}

public static class UserClaims
{
    // Short claim names are used because OpenID Connect claim mapping is switched off.
    public const string ObjectId = "oid";
    public const string Name = "name";
    public const string Username = "preferred_username";
    public const string Groups = "groups";

    public static UserContext ToContext(ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ObjectId) ?? p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var name = p.FindFirstValue(Name) ?? p.Identity?.Name ?? p.FindFirstValue(Username) ?? id;
        var groups = p.FindAll(Groups).Select(c => c.Value).ToArray();
        return new UserContext(id, name, groups);
    }
}

public sealed class AccessService(IDbContextFactory<AppDbContext> dbf, TimeProvider clock, Microsoft.Extensions.Options.IOptions<PortalAuthOptions> options)
{
    public async Task<AccessEvaluator> GetEvaluatorAsync(ClaimsPrincipal principal)
    {
        var user = UserClaims.ToContext(principal);
        var now = clock.GetUtcNow();
        if (string.IsNullOrEmpty(user.UserId)) return new AccessEvaluator(user, [], now);

        var ids = user.GroupIds.Append(user.UserId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var db = await dbf.CreateDbContextAsync();
        var rows = await db.RoleAssignments.AsNoTracking().Where(a => a.RevokedAt == null && ids.Contains(a.PrincipalId)).ToListAsync();

        var username = principal.FindFirstValue(UserClaims.Username);
        var isBootstrap = options.Value.BootstrapAdmins.Any(b =>
            string.Equals(b, user.UserId, StringComparison.OrdinalIgnoreCase) ||
            (username is not null && string.Equals(b, username, StringComparison.OrdinalIgnoreCase)));
        if (isBootstrap)
            rows.Add(new RoleAssignment
            {
                Id = Guid.Empty, PrincipalType = PrincipalType.User, PrincipalId = user.UserId, PrincipalName = user.DisplayName,
                Scope = RoleScope.Platform, Role = Role.PlatformAdmin, GrantedBy = "bootstrap", GrantedAt = now,
            });
        return new AccessEvaluator(user, rows, now);
    }
}

/// <summary>Scoped per circuit/request. Gives services a ready Actor for the signed-in user.</summary>
public sealed class ActorProvider(
    AuthenticationStateProvider authState,
    AccessService access,
    IHttpContextAccessor http)
{
    private string? _ip;

    public async Task<Actor> GetAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        var principal = state.User;
        _ip ??= http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        var evaluator = await access.GetEvaluatorAsync(principal);
        var u = UserClaims.ToContext(principal);
        return new Actor(u.UserId, u.DisplayName, _ip, evaluator);
    }
}

public sealed class PermissionRequirement(Permission permission) : IAuthorizationRequirement
{
    public Permission Permission { get; } = permission;
}

public sealed class PermissionHandler(AccessService access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        var evaluator = await access.GetEvaluatorAsync(context.User);
        if (evaluator.Can(requirement.Permission)) context.Succeed(requirement);
    }
}

public static class Policies
{
    public static string For(Permission p) => $"perm:{p}";
}
