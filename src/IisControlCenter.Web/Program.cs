using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Hangfire;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Auth;
using IisControlCenter.Web.Components;
using IisControlCenter.Web.Deployments;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
var isDev = builder.Environment.IsDevelopment();

// ---------- configuration checks (fail closed) ----------
var authMode = builder.Configuration["Auth:Mode"] ?? "";
if (!isDev && authMode != "Entra")
    throw new InvalidOperationException("Auth:Mode must be 'Entra' outside Development.");
if (authMode == "Dev" && !isDev)
    throw new InvalidOperationException("Dev sign-in is only allowed in the Development environment.");
if (authMode is not ("Entra" or "Dev"))
    throw new InvalidOperationException("Set Auth:Mode to 'Entra' (or 'Dev' in Development).");
var agentsHost = builder.Configuration["Portal:AgentsHost"];
if (!isDev && string.IsNullOrWhiteSpace(agentsHost))
    throw new InvalidOperationException("Portal:AgentsHost is required outside Development.");

TlsSetup.Configure(builder);

// ---------- services ----------
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

var provider = builder.Configuration["Database:Provider"] ?? "SqlServer";
if (provider == "Sqlite" && !isDev) throw new InvalidOperationException("SQLite is for Development only.");
var conn = builder.Configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is missing.");
builder.Services.AddDbContextFactory<AppDbContext>(o =>
{
    if (provider == "Sqlite") o.UseSqlite(conn); else o.UseSqlServer(conn);
});

builder.Services.Configure<PortalAuthOptions>(builder.Configuration.GetSection(PortalAuthOptions.Section));

var auth = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = authMode == "Entra" ? OpenIdConnectDefaults.AuthenticationScheme : CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/access-denied";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = false;
})
.AddScheme<AuthenticationSchemeOptions, AgentCertAuthHandler>(AgentCertAuth.Scheme, _ => { });

if (authMode == "Entra")
{
    var ad = builder.Configuration.GetSection("AzureAd");
    auth.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.Authority = $"https://login.microsoftonline.com/{ad["TenantId"]}/v2.0";
        o.ClientId = ad["ClientId"];
        o.ClientSecret = ad["ClientSecret"];   // supply from Key Vault / DPAPI-protected source, not appsettings.json
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.MapInboundClaims = false;            // keep short claim names: oid, name, groups
        o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
        o.TokenValidationParameters.NameClaimType = "name";
        o.CallbackPath = "/signin-oidc";
    });
}

builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    foreach (var p in Enum.GetValues<Permission>())
        o.AddPolicy(Policies.For(p), pb => pb.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(p)));
});
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddScoped<ActorProvider>();

builder.Services.AddSingleton<AgentConnectionTracker>();
builder.Services.AddSingleton<ServerEventBus>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddScoped<AgentCommandService>();
builder.Services.AddScoped<ServerService>();
builder.Services.AddScoped<ServerQueries>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<AuditQueries>();
builder.Services.AddScoped<DiscoveryJob>();
builder.Services.AddScoped<ReconciliationJob>();
builder.Services.AddScoped<Reconciler>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<PoolAdminService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<TargetService>();
builder.Services.AddScoped<DriftQueries>();
builder.Services.AddHostedService<AgentMonitor>();

// ---------- deployments ----------
builder.Services.Configure<DeploymentOptions>(builder.Configuration.GetSection(DeploymentOptions.Section));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));
builder.Services.AddSingleton<IPackageStore, FilePackageStore>();
builder.Services.AddSingleton<IPortalAntivirus, DefenderPortalAntivirus>();
builder.Services.AddSingleton<PendingAgentResults>();
builder.Services.AddSingleton<DeploymentEventBus>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<DiffService>();
builder.Services.AddScoped<ProtectedListService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<DeploymentService>();
builder.Services.AddScoped<DeploymentOrchestrator>();
builder.Services.AddScoped<RunReportService>();
builder.Services.AddScoped<MaintenanceService>();
builder.Services.AddHostedService<DeploymentWorker>();

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 4 * 1024 * 1024;     // discovery payloads and log batches (packages and manifests go over HTTPS)
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("register", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var hangfireOn = builder.Configuration.GetValue("Hangfire:Enabled", true);
if (hangfireOn)
{
    builder.Services.AddHangfire(c => c.UseSqlServerStorage(conn));
    builder.Services.AddHangfireServer();
}

var app = builder.Build();

// ---------- database ----------
if (builder.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    var dbf = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    if (provider == "Sqlite") await db.Database.EnsureCreatedAsync(); else await db.Database.MigrateAsync();
    if (authMode == "Dev") await DevSeed.RunAsync(db);
}

// ---------- pipeline ----------
if (!isDev)
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Host separation (D2): the agents name serves only agent endpoints, the UI name never serves them.
if (!string.IsNullOrWhiteSpace(agentsHost))
{
    app.Use(async (ctx, next) =>
    {
        var onAgents = string.Equals(ctx.Request.Host.Host, agentsHost, StringComparison.OrdinalIgnoreCase);
        var agentPath = ctx.Request.Path.StartsWithSegments(ContractInfo.Routes.AgentHub)
                        || ctx.Request.Path.StartsWithSegments("/api/agent");
        if (onAgents != agentPath) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        await next();
    });
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();   // css and js must load before sign-in
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// ---------- agent endpoints ----------
app.MapPost(ContractInfo.Routes.Register, async (RegisterRequest req, ServerService svc, HttpContext ctx) =>
{
    var presented = ctx.Connection.ClientCertificate is { } c ? ServerService.Thumbprint(c) : null;
    var r = await svc.RegisterAgentAsync(req, ctx.Connection.RemoteIpAddress?.ToString(), presented);
    return r.Ok ? Results.Ok(r.Value) : Results.BadRequest(new { error = r.Error });
}).AllowAnonymous().RequireRateLimiting("register").DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(64 * 1024));

app.MapDeploymentEndpoints();

app.MapHub<AgentHub>(ContractInfo.Routes.AgentHub);   // [Authorize(AgentCert)] is on the hub. No AllowAnonymous here: it would override it.

// ---------- sign-in / sign-out ----------
app.MapGet("/login", (string? returnUrl, HttpContext ctx) =>
{
    var target = IsLocal(returnUrl) ? returnUrl! : "/";
    if (authMode == "Entra")
        return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
    return Results.Redirect("/dev-login?returnUrl=" + Uri.EscapeDataString(target));
}).AllowAnonymous();

app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/signed-out");
}).AllowAnonymous();

if (authMode == "Dev")
{
    app.MapGet("/dev-login/{persona}", async (string persona, string? returnUrl, HttpContext ctx) =>
    {
        var p = DevSeed.Personas.FirstOrDefault(x => x.Key == persona);
        if (p.Key is null) return Results.NotFound();
        var id = new ClaimsIdentity(
        [
            new Claim("oid", p.Oid), new Claim("name", p.Name), new Claim("preferred_username", p.Key),
        ], CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
        return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/");
    }).AllowAnonymous();
}

// ---------- automated end-to-end test hooks (Development + Dev sign-in only; never mapped otherwise) ----------
if (authMode == "Dev" && isDev)
{
    static Actor E2EActor(TimeProvider clock)
    {
        var user = new UserContext("e2e", "E2E test", []);
        var admin = new RoleAssignment { Id = Guid.NewGuid(), PrincipalType = PrincipalType.User, PrincipalId = "e2e", Scope = RoleScope.Platform, Role = Role.PlatformAdmin };
        return new Actor("e2e", "E2E test", null, new AccessEvaluator(user, [admin], clock.GetUtcNow()));
    }
    app.MapPost("/dev/e2e/new-server", async (string name, ServerService svc, TimeProvider clock) =>
    {
        var r = await svc.CreateAsync(E2EActor(clock), name);
        return r.Ok ? Results.Ok(new { serverId = r.Value!.ServerId, token = r.Value.Token }) : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/approve", async (Guid serverId, string environment, ServerService svc, TimeProvider clock) =>
    {
        var r = await svc.ApproveAsync(E2EActor(clock), serverId, environment);
        return r.Ok ? Results.Ok() : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/project", async (string name, string? health, ProjectService projects, TimeProvider clock) =>
    {
        var a = E2EActor(clock);
        var p = await projects.CreateAsync(a, name, null, null, null);
        if (!p.Ok) return Results.BadRequest(p.Error);
        var c = await projects.AddComponentAsync(a, p.Value, "Web", "Web", health ?? "/", null, false);
        return c.Ok ? Results.Ok(new { projectId = p.Value, componentId = c.Value }) : Results.BadRequest(c.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/link", async (Guid componentId, Guid serverId, long siteId, string? appPath, TargetService targets, TimeProvider clock) =>
    {
        var r = await targets.LinkAsync(E2EActor(clock), componentId, serverId, siteId, appPath ?? "/");
        return r.Ok ? Results.Ok(new { targetId = r.Value }) : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/grant", async (string persona, Guid projectId, string role, string env, MembershipService members, TimeProvider clock) =>
    {
        var p = DevSeed.Personas.FirstOrDefault(x => x.Key == persona);
        if (p.Key is null || !Enum.TryParse<Role>(role, out var rl)) return Results.BadRequest("unknown persona or role");
        var r = await members.GrantAsync(E2EActor(clock), projectId, PrincipalType.User, p.Oid, p.Name, rl, env, DateTimeOffset.UtcNow.AddDays(30));
        return r.Ok ? Results.Ok() : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    static async Task<Actor> PersonaActor(string persona, AccessService access)
    {
        var p = DevSeed.Personas.First(x => x.Key == persona);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", p.Oid), new Claim("name", p.Name)], "dev", "name", ClaimTypes.Role));
        var u = UserClaims.ToContext(principal);
        return new Actor(u.UserId, u.DisplayName, null, await access.GetEvaluatorAsync(principal), persona + "@dev.local");
    }
    // These two call the real services with a real permission check, as the given persona (the browser UI does the same through the pages).
    app.MapPost("/dev/e2e/submit", async (string persona, Guid componentId, string env, Guid packageId, Guid targetId, string source, DeploymentService deployments, AccessService access) =>
    {
        var r = await deployments.SubmitAsync(await PersonaActor(persona, access),
            new SubmitCommand(componentId, env, packageId, [targetId], IisControlCenter.Contracts.Deployments.DeployMode.Overlay, source, null, null, null, [], null));
        return r.Ok ? Results.Ok(new { requestId = r.Value }) : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/approve-request", async (string persona, Guid id, DeploymentService deployments, AccessService access) =>
    {
        var r = await deployments.ApproveAsync(await PersonaActor(persona, access), id);
        return r.Ok ? Results.Ok() : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapPost("/dev/e2e/mode", async (Guid serverId, string mode, ServerService svc, TimeProvider clock) =>
    {
        var r = await svc.SetModeAsync(E2EActor(clock), serverId, Enum.Parse<ServerMode>(mode));
        return r.Ok ? Results.Ok() : Results.BadRequest(r.Error);
    }).AllowAnonymous();
    app.MapGet("/dev/e2e/request", async (Guid id, IDbContextFactory<AppDbContext> dbf) =>
    {
        await using var db = await dbf.CreateDbContextAsync();
        var r = await db.DeploymentRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return Results.NotFound();
        var runs = await db.DeploymentRuns.AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Sequence)
            .Select(x => new { x.Status, x.CurrentStep, x.FailureStep, x.Message, x.HealthDetail, x.BackupId }).ToListAsync();
        return Results.Ok(new { status = r.Status.ToString(), detail = r.StatusDetail, selfApproved = r.SelfApproved, direct = r.IsDirectToProd, runs });
    }).AllowAnonymous();
    app.MapGet("/dev/e2e/status", async (Guid serverId, IDbContextFactory<AppDbContext> dbf) =>
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return Results.NotFound();
        return Results.Ok(new
        {
            agentStatus = s.AgentStatus.ToString(), environment = s.EnvironmentName, declared = s.DeclaredEnvironment, mode = s.Mode.ToString(),
            hostname = s.Hostname, iisInstalled = s.IisInstalled, lastDiscoveryAt = s.LastDiscoveryAt,
            sites = await db.IisSites.CountAsync(x => x.ServerId == serverId), pools = await db.IisAppPools.CountAsync(x => x.ServerId == serverId),
            siteNames = await db.IisSites.Where(x => x.ServerId == serverId).Select(x => x.Name).ToListAsync(),
        });
    }).AllowAnonymous();
}

// ---------- exports ----------
app.MapGet("/export/audit.csv", async (string? search, string? action, string? result, HttpContext ctx,
    AccessService access, AuditQueries queries, TimeProvider clock) =>
{
    var evaluator = await access.GetEvaluatorAsync(ctx.User);
    var u = UserClaims.ToContext(ctx.User);
    var actor = new Actor(u.UserId, u.DisplayName, ctx.Connection.RemoteIpAddress?.ToString(), evaluator);
    try
    {
        var csv = await queries.ExportCsvAsync(actor, new AuditFilter(search, action, result, null, null));
        return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"audit-{clock.GetUtcNow():yyyyMMdd-HHmm}.csv");
    }
    catch (ForbiddenException) { return Results.Forbid(); }
}).RequireAuthorization(Policies.For(Permission.ViewAudit));

// ---------- jobs ----------
if (hangfireOn)
{
    app.MapHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [] })
        .RequireAuthorization(Policies.For(Permission.ManageSettings));
    RecurringJob.AddOrUpdate<DiscoveryJob>("discovery-all", j => j.RunAll(), "0 */6 * * *");
    RecurringJob.AddOrUpdate<ReconciliationJob>("reconcile-daily", j => j.RunAll(), "30 2 * * *");   // 02:30 UTC
}

// Component endpoints (including /_blazor) must be reachable before sign-in; every routable page carries [Authorize] or [AllowAnonymous]
// (a unit test enforces this), and AuthorizeRouteView applies it.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AllowAnonymous();
app.Run();

static bool IsLocal(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

public partial class Program;
