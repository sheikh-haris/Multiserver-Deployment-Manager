using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Deployments;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Auth;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Deployments;

public sealed record StartUploadBody(Guid ComponentId, string Environment, string FileName, long Size);

public static class DeploymentEndpoints
{
    /// <summary>Browsers cannot add a custom header to a cross-site request without a CORS preflight, and this site allows no CORS. So the header proves the call came from our own page.</summary>
    public const string CsrfHeader = "X-ICC-Request";

    private static readonly AuthorizeAttribute AgentAuth = new() { AuthenticationSchemes = AgentCertAuth.Scheme };

    private static Guid AgentServerId(HttpContext c) => Guid.Parse(c.User.FindFirstValue(AgentCertAuth.ServerIdClaim)!);

    private static async Task<Actor> ActorFor(HttpContext ctx)
    {
        var access = ctx.RequestServices.GetRequiredService<AccessService>();
        var evaluator = await access.GetEvaluatorAsync(ctx.User);
        var u = UserClaims.ToContext(ctx.User);
        var email = ctx.User.FindFirstValue("email") ?? (ctx.User.FindFirstValue(UserClaims.Username) is { } upn && upn.Contains('@') ? upn : null);
        return new Actor(u.UserId, u.DisplayName, ctx.Connection.RemoteIpAddress?.ToString(), evaluator, email);
    }

    private static bool HasCsrfHeader(HttpContext c) => c.Request.Headers.ContainsKey(CsrfHeader);

    public static void MapDeploymentEndpoints(this WebApplication app)
    {
        // ---------------- agent side (client certificate) ----------------

        app.MapGet($"{ContractInfo.Routes.AgentPackages}/{{packageId:guid}}", async (Guid packageId, Guid run, HttpContext ctx, IDbContextFactory<AppDbContext> dbf, IPackageStore store) =>
        {
            var serverId = AgentServerId(ctx);
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.DeploymentRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == run);
            if (r is null || r.Status != RunStatus.Running) return Results.NotFound();
            var target = await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == r.DeploymentTargetId);
            if (target.ServerId != serverId) return Results.NotFound();
            var req = await db.DeploymentRequests.AsNoTracking().FirstAsync(x => x.Id == r.RequestId);
            if (req.Type != RequestType.Deploy || req.PackageId != packageId) return Results.NotFound();
            var pkg = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId && p.DeletedAt == null);
            if (pkg is null || !store.Exists(pkg)) return Results.NotFound();
            return Results.File(store.OpenRead(pkg), "application/zip", enableRangeProcessing: true);
        }).RequireAuthorization(AgentAuth).DisableAntiforgery();

        app.MapGet($"{ContractInfo.Routes.AgentRuns}/{{runId:guid}}/plan", async (Guid runId, HttpContext ctx, IDbContextFactory<AppDbContext> dbf) =>
        {
            var serverId = AgentServerId(ctx);
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.DeploymentRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId);
            if (r is null) return Results.NotFound();
            var target = await db.DeploymentTargets.AsNoTracking().FirstAsync(t => t.Id == r.DeploymentTargetId);
            if (target.ServerId != serverId) return Results.NotFound();
            var rt = await db.RequestTargets.AsNoTracking().FirstOrDefaultAsync(t => t.RequestId == r.RequestId && t.DeploymentTargetId == r.DeploymentTargetId);
            if (rt is null) return Results.NotFound();
            return Results.Ok(new RunPlanInput(
                JsonSerializer.Deserialize<List<SnapshotFile>>(rt.SnapshotJson) ?? [],
                JsonSerializer.Deserialize<List<string>>(rt.DeletesJson) ?? []));
        }).RequireAuthorization(AgentAuth).DisableAntiforgery();

        app.MapGet($"{ContractInfo.Routes.AgentResults}/manifest-input/{{commandId:guid}}", (Guid commandId, HttpContext ctx, PendingAgentResults pending) =>
            pending.TryGetManifestInput(commandId, AgentServerId(ctx), out var paths) ? Results.Ok(paths) : Results.NotFound())
            .RequireAuthorization(AgentAuth).DisableAntiforgery();

        app.MapPut($"{ContractInfo.Routes.AgentResults}/manifest/{{commandId:guid}}", async (Guid commandId, HttpContext ctx, PendingAgentResults pending) =>
        {
            Stream body = ctx.Request.Body;
            if (ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase)) body = new GZipStream(body, CompressionMode.Decompress);
            ManifestResult? result;
            try { result = await JsonSerializer.DeserializeAsync<ManifestResult>(body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(); }
            if (result is null || result.CommandId != commandId) return Results.BadRequest();
            return pending.CompleteManifest(AgentServerId(ctx), result) ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization(AgentAuth).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(256L * 1024 * 1024));

        // ---------------- browser side (signed-in user, cookie) ----------------

        app.MapPost("/api/uploads", async (StartUploadBody body, HttpContext ctx, UploadService uploads) =>
        {
            if (!HasCsrfHeader(ctx)) return Results.BadRequest(new { error = "Missing header." });
            try
            {
                var r = await uploads.StartAsync(await ActorFor(ctx), body.ComponentId, body.Environment, body.FileName, body.Size);
                return r.Ok ? Results.Ok(r.Value) : Results.BadRequest(new { error = r.Error });
            }
            catch (ForbiddenException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        }).DisableAntiforgery();

        app.MapPut("/api/uploads/{id:guid}/chunks/{index:int}", async (Guid id, int index, HttpContext ctx, UploadService uploads) =>
        {
            if (!HasCsrfHeader(ctx)) return Results.BadRequest(new { error = "Missing header." });
            var r = await uploads.ReceiveChunkAsync(await ActorFor(ctx), id, index, ctx.Request.Body, ctx.Request.Headers["X-Chunk-Sha256"].FirstOrDefault(), ctx.RequestAborted);
            return r.Ok ? Results.Ok(new { received = r.Value }) : Results.BadRequest(new { error = r.Error });
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));

        app.MapPost("/api/uploads/{id:guid}/complete", async (Guid id, HttpContext ctx, UploadService uploads) =>
        {
            if (!HasCsrfHeader(ctx)) return Results.BadRequest(new { error = "Missing header." });
            var r = await uploads.CompleteAsync(await ActorFor(ctx), id);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        }).DisableAntiforgery();

        app.MapGet("/api/uploads/{id:guid}", async (Guid id, HttpContext ctx, UploadService uploads) =>
        {
            var i = await uploads.GetAsync(await ActorFor(ctx), id);
            return i is null ? Results.NotFound() : Results.Ok(i);
        });

        // A Platform Admin can download a stored package (for investigation). Always audited.
        app.MapGet("/api/packages/{id:guid}/download", async (Guid id, HttpContext ctx, IDbContextFactory<AppDbContext> dbf, IPackageStore store, AuditService audit) =>
        {
            var actor = await ActorFor(ctx);
            if (!actor.Can(Permission.ManageProjects)) return Results.Forbid();
            await using var db = await dbf.CreateDbContextAsync();
            var p = await db.Packages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
            if (p is null || !store.Exists(p)) return Results.NotFound();
            await audit.WriteAsync(actor, new AuditRecord("Package.Download", "Package", id.ToString(), After: p.FileName));
            return Results.File(store.OpenRead(p), "application/zip", p.FileName);
        });
    }
}
