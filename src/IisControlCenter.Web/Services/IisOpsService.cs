using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using IisControlCenter.Web.Deployments;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record IisOutcome(bool Ok, string? Error, bool Queued, Guid? RequestId, IisOpResult? Result);

public sealed record IisSiteRow(long SiteId, string Name, string State, string? Folder, string? Pool, IReadOnlyList<HttpBinding> Http, IReadOnlyList<string> OtherBindings,
    IReadOnlyList<(string Path, string? Folder, string? Pool)> Apps, bool Owned, bool Linked);

public sealed record IisPage(Server Server, IReadOnlyList<IisSiteRow> Sites, IReadOnlyList<string> Pools, bool CanAct, string? WhyNot, bool OwnerMode, Guid? ProjectId, bool CanModify);

/// <summary>
/// Create pools, sites, applications, virtual directories and folders; start or stop a site; replace HTTP bindings; give an app pool account access to a folder.
/// No delete. Operators (ManageIis) may do all of it; in Production a second person must approve everything except Start.
/// A project Owner may create and manage their OWN items in Dev and Staging on servers where their project already has a target.
/// </summary>
public sealed class IisOpsService(
    IDbContextFactory<AppDbContext> dbf,
    AgentCommandService commands,
    PendingAgentResults pending,
    AuditService audit,
    ServerEventBus bus,
    TimeProvider clock)
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // ======================= rules that need no agent =======================

    private static string? WhyNot(Server s, bool online)
    {
        if (s.EnvironmentName is null) return "The server is not approved yet.";
        if (s.Mode != ServerMode.Managed) return "The server is in ReadOnly mode. A Platform Admin can switch it to Managed, if the agent was installed with Managed mode allowed.";
        if (!online) return "The agent is offline.";
        return null;
    }

    /// <summary>Cheap checks on the shape of the request. The agent repeats all of them and also checks what exists in IIS.</summary>
    public static string? Validate(IisOpCommand c, bool allowNewSiteMarker = false)
    {
        var e = new List<string>();
        bool BadSite() => c.SiteId <= 0 && !(allowNewSiteMarker && c.SiteId == IisOpCommand.NewSiteMarker);
        switch (c.Op)
        {
            case IisOp.CreatePool:
                if (PoolRules.ValidateName(c.PoolName) is { } a) e.Add(a);
                if (c.PoolConfig is { IsEmpty: false } cfg) e.AddRange(PoolRules.Validate(cfg));
                break;
            case IisOp.CreateSite:
                if (IisRules.ValidateSiteName(c.SiteName) is { } b) e.Add(b);
                e.AddRange(IisRules.ValidateBindings(c.Bindings));
                if (string.IsNullOrWhiteSpace(c.PoolName)) e.Add("Choose an app pool.");
                if (string.IsNullOrWhiteSpace(c.PhysicalPath)) e.Add("Enter the folder.");
                break;
            case IisOp.StartSite or IisOp.StopSite:
                if (BadSite()) e.Add("Choose a site.");
                break;
            case IisOp.UpdateBindings:
                if (BadSite()) e.Add("Choose a site.");
                e.AddRange(IisRules.ValidateBindings(c.Bindings));
                break;
            case IisOp.CreateApplication:
                if (BadSite()) e.Add("Choose a site.");
                if (IisRules.ValidateVirtualPath(IisRules.NormalizeVirtualPath(c.AppPath ?? ""), "application") is { } p) e.Add(p);
                if (string.IsNullOrWhiteSpace(c.PoolName)) e.Add("Choose an app pool.");
                if (string.IsNullOrWhiteSpace(c.PhysicalPath)) e.Add("Enter the folder.");
                break;
            case IisOp.CreateVirtualDirectory:
                if (BadSite()) e.Add("Choose a site.");
                if (IisRules.ValidateVirtualPath(IisRules.NormalizeVirtualPath(c.VirtualPath ?? ""), "virtual directory") is { } v) e.Add(v);
                if (string.IsNullOrWhiteSpace(c.PhysicalPath)) e.Add("Enter the folder.");
                break;
            case IisOp.CreateFolder:
                if (string.IsNullOrWhiteSpace(c.PhysicalPath)) e.Add("Enter the folder.");
                break;
            case IisOp.GrantFolderAccess:
                if (PoolRules.ValidateName(c.PoolName) is { } n) e.Add(n);
                if (IisRules.ValidateAccess(c.Access) is { } ac) e.Add(ac);
                if (string.IsNullOrWhiteSpace(c.PhysicalPath)) e.Add("Enter the folder.");
                break;
            default: e.Add("Unknown operation.");
                break;
        }
        return e.Count == 0 ? null : string.Join(" ", e);
    }

    public static string Describe(IisOpCommand c) => c.Op switch
    {
        IisOp.CreatePool => $"Create app pool {c.PoolName}",
        IisOp.CreateSite => $"Create site {c.SiteName} → {c.PhysicalPath}, pool {c.PoolName}, {string.Join(", ", (c.Bindings ?? []).Select(b => $"{b.Host}:{b.Port}"))} (starts stopped)",
        IisOp.StartSite => $"Start site #{c.SiteId}",
        IisOp.StopSite => $"Stop site #{c.SiteId}",
        IisOp.UpdateBindings => $"Set bindings of site #{c.SiteId} to {string.Join(", ", (c.Bindings ?? []).Select(b => $"{b.Host}:{b.Port}"))}",
        IisOp.CreateApplication => $"Create application {c.AppPath} on {SiteLabel(c.SiteId)} → {c.PhysicalPath}, pool {c.PoolName}",
        IisOp.CreateVirtualDirectory => $"Create virtual directory {c.VirtualPath} on {SiteLabel(c.SiteId)} → {c.PhysicalPath}",
        IisOp.CreateFolder => $"Create folder {c.PhysicalPath}",
        IisOp.GrantFolderAccess => $"Give IIS AppPool\\{c.PoolName} {c.Access} access to {c.PhysicalPath}",
        _ => c.Op.ToString(),
    };

    private static string SiteLabel(long id) => id == IisOpCommand.NewSiteMarker ? "the new site" : $"site #{id}";

    private static bool Inside(string? path, string? parent)
    {
        static string N(string? p) => (p ?? "").Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var a = N(path); var b = N(parent);
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b + "/", StringComparison.Ordinal));
    }

    // ======================= who may do what =======================

    private sealed record Ownership(HashSet<long> Sites, HashSet<string> Pools, List<string> Paths, List<string> ForeignPaths);

    private static async Task<Ownership> OwnershipAsync(AppDbContext db, Guid serverId, Guid projectId)
    {
        var comps = await db.Components.AsNoTracking().ToListAsync();
        var mineIds = comps.Where(c => c.ProjectId == projectId).Select(c => c.Id).ToHashSet();
        var targets = await db.DeploymentTargets.AsNoTracking().Where(t => t.ServerId == serverId).ToListAsync();
        var ledger = await db.IisCreatedItems.AsNoTracking().Where(i => i.ServerId == serverId).ToListAsync();
        var sites = new HashSet<long>(); var pools = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var paths = new List<string>(); var foreign = new List<string>();
        foreach (var t in targets)
        {
            if (mineIds.Contains(t.ComponentId)) { sites.Add(t.IisSiteId); pools.Add(t.AppPoolName); paths.Add(t.PhysicalPath); }
            else foreign.Add(t.PhysicalPath);
        }
        foreach (var i in ledger)
        {
            if (i.ProjectId == projectId)
            {
                if (i.Kind == IisItemKind.Site && long.TryParse(i.Key.Split(':')[0], out var sid)) sites.Add(sid);
                if (i.Kind == IisItemKind.Pool) pools.Add(i.Key);
                if (i.Path is not null) paths.Add(i.Path);
            }
            else if (i.Path is not null) foreign.Add(i.Path);
        }
        // a pool that another project's target uses is not "mine", even if my target also uses it
        foreach (var t in targets.Where(t => !mineIds.Contains(t.ComponentId))) pools.Remove(t.AppPoolName);
        return new Ownership(sites, pools, paths, foreign);
    }

    private sealed record Auth(bool Allowed, bool ViaOwner, string? Error, Guid? ProjectId);

    private async Task<Auth> AuthorizeAsync(AppDbContext db, Actor actor, Server server, IisOpCommand c, Guid? projectId)
    {
        var env = server.EnvironmentName;
        if (actor.Can(Permission.ManageIis, env)) return new(true, false, null, projectId);

        // project Owner path: Dev and Staging only, their own project, a server where the project already has a target
        if (projectId is not { } pid || !actor.Access.IsProjectOwner(pid))
            return new(false, false, "You need the Server Operator role for this environment, or Platform Admin. (Project Owners can do this for their own project in Dev and Staging.)", null);
        if (env == EnvironmentNames.Prod) return new(false, true, "In Production only a Server Operator or Platform Admin can change IIS.", pid);
        var comps = await db.Components.AsNoTracking().Where(x => x.ProjectId == pid).Select(x => x.Id).ToListAsync();
        if (!await db.DeploymentTargets.AnyAsync(t => comps.Contains(t.ComponentId) && t.ServerId == server.Id))
            return new(false, true, "Your project has no deployment target on this server yet. Ask a Server Operator to create the first site, then map it to your component.", pid);

        var own = await OwnershipAsync(db, server.Id, pid);
        string? deny = c.Op switch
        {
            IisOp.CreatePool => null,
            IisOp.CreateSite => !own.Pools.Contains(c.PoolName ?? "") ? "Use an app pool of your project (create one first)." : ForeignFolder(c, own),
            IisOp.CreateApplication => !own.Sites.Contains(c.SiteId) ? "That site does not belong to your project." : !own.Pools.Contains(c.PoolName ?? "") ? "Use an app pool of your project." : ForeignFolder(c, own),
            IisOp.CreateVirtualDirectory => !own.Sites.Contains(c.SiteId) ? "That site does not belong to your project." : ForeignFolder(c, own),
            IisOp.StartSite or IisOp.StopSite or IisOp.UpdateBindings => own.Sites.Contains(c.SiteId) ? null : "That site does not belong to your project.",
            IisOp.CreateFolder => own.Paths.Any(p => Inside(c.PhysicalPath, p)) ? null : "You can only create folders inside folders of your project.",
            IisOp.GrantFolderAccess => c.Access == FolderAccess.Modify ? "Only a Server Operator or Platform Admin can grant Modify."
                : !own.Pools.Contains(c.PoolName ?? "") ? "You can only give access to an app pool of your project."
                : own.Paths.Any(p => Inside(c.PhysicalPath, p)) ? null : "You can only change access on folders of your project.",
            _ => "Not allowed.",
        };
        return deny is null ? new(true, true, null, pid) : new(false, true, deny, pid);
    }

    private static string? ForeignFolder(IisOpCommand c, Ownership own) =>
        own.ForeignPaths.Any(f => Inside(c.PhysicalPath, f)) ? "That folder is inside a folder that belongs to another project." : null;

    private static bool NeedsApproval(Server s, IisOp op) => s.EnvironmentName == EnvironmentNames.Prod && op != IisOp.StartSite;

    // ======================= run =======================

    public async Task<IisOutcome> RunAsync(Actor actor, Guid serverId, IisOpCommand command, Guid? projectId, string? reason, CancellationToken ct = default)
    {
        if (Validate(command) is { } bad) return new(false, bad, false, null, null);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId && !s.IsDeleted, ct);
        if (server is null) return new(false, "Server not found.", false, null, null);
        var auth = await AuthorizeAsync(db, actor, server, command, projectId);
        var action = AuditAction(command);
        if (!auth.Allowed)
        {
            await audit.WriteAsync(actor, new AuditRecord(action, "IisObject", null, serverId, server.EnvironmentName, Result: "Denied", Detail: auth.Error));
            if (auth.Error!.StartsWith("You need")) throw new ForbiddenException(auth.Error);
            return new(false, auth.Error, false, null, null);
        }
        if (WhyNot(server, commands.IsConnected(serverId)) is { } why) return new(false, why, false, null, null);
        if (command.Op == IisOp.GrantFolderAccess && command.Access == FolderAccess.Modify && string.IsNullOrWhiteSpace(reason))
            return new(false, "Modify access needs a written reason.", false, null, null);
        if (auth.ViaOwner) command = command with { RequireNewFolder = command.Op is IisOp.CreateSite or IisOp.CreateApplication or IisOp.CreateVirtualDirectory, CreateFolder = command.CreateFolder || command.Op is IisOp.CreateSite or IisOp.CreateApplication or IisOp.CreateVirtualDirectory };

        if (NeedsApproval(server, command.Op))
        {
            if (string.IsNullOrWhiteSpace(reason)) return new(false, "Production changes need a written reason: a second person approves them.", false, null, null);
            var now = clock.GetUtcNow();
            var req = new PoolChangeRequest
            {
                Id = Guid.NewGuid(), ServerId = serverId, EnvironmentName = server.EnvironmentName!, Kind = PoolRequestKind.Iis, PoolsJson = "[]",
                PayloadJson = JsonSerializer.Serialize(command, Json), Summary = Describe(command), Reason = reason.Trim(),
                RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedAt = now, ExpiresAt = now + PoolAdminService.RequestLifetime, ProjectId = auth.ProjectId,
            };
            db.PoolChangeRequests.Add(req);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, new AuditRecord("Pool.Request", "PoolChangeRequest", req.Id.ToString(), serverId, server.EnvironmentName, After: req.Summary, Detail: reason.Trim()));
            bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
            return new(true, null, true, req.Id, null);
        }
        return await ExecuteAsync(db, actor, server, command, auth.ProjectId, reason, ct);
    }

    // ======================= batches (templates) =======================

    public sealed record BatchStep(string Description, bool Ok, string? Message);
    public sealed record BatchOutcome(bool Ok, string? Error, bool Queued, Guid? RequestId, IReadOnlyList<BatchStep> Steps);

    /// <summary>
    /// Several operations as one unit, for example "apply a site template". Everything is validated first.
    /// Outside Production the steps run in order and stop at the first failure (nothing is undone: there is no delete).
    /// In Production the whole list is ONE request: a second person sees every step and approves them together.
    /// </summary>
    public async Task<BatchOutcome> RunBatchAsync(Actor actor, Guid serverId, IReadOnlyList<IisOpCommand> commands0, Guid? projectId, string? reason, string summary, CancellationToken ct = default)
    {
        BatchOutcome Fail(string e) => new(false, e, false, null, []);
        if (commands0.Count == 0) return Fail("Nothing to do.");
        var seenSite = false;
        foreach (var c in commands0)
        {
            if (c.SiteId == IisOpCommand.NewSiteMarker && !seenSite) return Fail("Internal error: a step refers to a site that is not created yet.");
            if (Validate(c, allowNewSiteMarker: true) is { } bad) return Fail($"{Describe(c)}: {bad}");
            if (c.Op == IisOp.CreateSite) seenSite = true;
        }
        await using var db = await dbf.CreateDbContextAsync(ct);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId && !s.IsDeleted, ct);
        if (server is null) return Fail("Server not found.");
        if (WhyNot(server, commands.IsConnected(serverId)) is { } why) return Fail(why);

        var isOperator = actor.Can(Permission.ManageIis, server.EnvironmentName);
        var hasModify = commands0.Any(c => c.Op == IisOp.GrantFolderAccess && c.Access == FolderAccess.Modify);
        if (hasModify && string.IsNullOrWhiteSpace(reason)) return Fail("This includes Modify access. Write the reason.");
        if (!isOperator)
        {
            // Owner path: the same checks as single operations, plus the whole batch is refused up front if any step could not be allowed
            if (projectId is not { } pid || !actor.Access.IsProjectOwner(pid) || server.EnvironmentName == EnvironmentNames.Prod)
            {
                await audit.WriteAsync(actor, new AuditRecord("Iis.Batch", "IisObject", null, serverId, server.EnvironmentName, Result: "Denied", Detail: summary));
                throw new ForbiddenException("You need the Server Operator role for this environment, or Platform Admin. (Project Owners can do this for their own project in Dev and Staging.)");
            }
            if (hasModify) return Fail("Only a Server Operator or Platform Admin can grant Modify. This template includes Modify access.");
        }

        if (isOperator && server.EnvironmentName == EnvironmentNames.Prod)
        {
            if (string.IsNullOrWhiteSpace(reason)) return Fail("Production changes need a written reason: a second person approves them.");
            var now = clock.GetUtcNow();
            var req = new PoolChangeRequest
            {
                Id = Guid.NewGuid(), ServerId = serverId, EnvironmentName = server.EnvironmentName!, Kind = PoolRequestKind.Iis, PoolsJson = "[]",
                PayloadJson = JsonSerializer.Serialize(commands0, Json), Summary = $"{summary} ({commands0.Count} steps: {string.Join("; ", commands0.Select(Describe))})", Reason = reason.Trim(),
                RequestedBy = actor.UserName, RequestedById = actor.UserId, RequestedAt = now, ExpiresAt = now + PoolAdminService.RequestLifetime, ProjectId = projectId,
            };
            if (req.Summary.Length > 500) req.Summary = req.Summary[..497] + "…";
            db.PoolChangeRequests.Add(req);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, new AuditRecord("Pool.Request", "PoolChangeRequest", req.Id.ToString(), serverId, server.EnvironmentName, After: string.Join(" | ", commands0.Select(Describe)), Detail: reason.Trim()));
            bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
            return new(true, null, true, req.Id, []);
        }

        var steps = new List<BatchStep>();
        long? createdSite = null;
        foreach (var c0 in commands0)
        {
            var c = c0.SiteId == IisOpCommand.NewSiteMarker ? c0 with { SiteId = createdSite ?? 0 } : c0;
            var o = await RunAsync(actor, serverId, c, projectId, reason, ct);
            steps.Add(new BatchStep(Describe(c0), o.Ok, o.Error ?? o.Result?.After));
            if (!o.Ok) return new(false, $"Stopped at: {Describe(c0)}. {o.Error} Earlier steps were done and are not undone.", false, null, steps);
            if (c.Op == IisOp.CreateSite) createdSite = o.Result?.SiteId;
        }
        await audit.WriteAsync(actor, new AuditRecord("Iis.Batch", "IisObject", null, serverId, server.EnvironmentName, After: $"{summary}: {steps.Count} steps done"));
        return new(true, null, false, null, steps);
    }

    /// <summary>Runs an approved batch in order. Authorization happened when it was requested; the approver is on the audit lines.</summary>
    private async Task<IisOutcome> ExecuteApprovedBatchAsync(AppDbContext db, Actor approver, Server server, PoolChangeRequest req, CancellationToken ct)
    {
        var cmds = JsonSerializer.Deserialize<List<IisOpCommand>>(req.PayloadJson!)!;
        long? createdSite = null; var done = new List<string>();
        foreach (var c0 in cmds)
        {
            var c = c0.SiteId == IisOpCommand.NewSiteMarker ? c0 with { SiteId = createdSite ?? 0 } : c0;
            var o = await ExecuteAsync(db, approver, server, c, req.ProjectId, $"Approved by {approver.UserName}; requested by {req.RequestedBy}: {req.Reason}", ct);
            if (!o.Ok) return new(false, $"Step {done.Count + 1} of {cmds.Count} failed ({Describe(c0)}): {o.Error}. {done.Count} step(s) were done before it and are not undone.", false, null, o.Result);
            done.Add(Describe(c0));
            if (c.Op == IisOp.CreateSite) createdSite = o.Result?.SiteId;
        }
        return new(true, null, false, null, new IisOpResult(Guid.Empty, true, null, null, $"{done.Count} steps done", true, createdSite));
    }

    internal static string AuditAction(IisOpCommand c) => c.Op == IisOp.GrantFolderAccess && c.Access == FolderAccess.Modify ? "Iis.GrantFolderAccess.Modify" : "Iis." + c.Op;

    /// <summary>Runs a request a second person approved. The approver's name goes on the audit line.</summary>
    internal async Task<IisOutcome> ExecuteApprovedAsync(AppDbContext db, Actor approver, Server server, PoolChangeRequest req, CancellationToken ct)
    {
        if (req.PayloadJson!.TrimStart().StartsWith('[')) return await ExecuteApprovedBatchAsync(db, approver, server, req, ct);
        var cmd = JsonSerializer.Deserialize<IisOpCommand>(req.PayloadJson!)!;
        return await ExecuteAsync(db, approver, server, cmd, req.ProjectId, $"Approved by {approver.UserName}; requested by {req.RequestedBy}: {req.Reason}", ct);
    }

    private async Task<IisOutcome> ExecuteAsync(AppDbContext db, Actor actor, Server server, IisOpCommand command, Guid? projectId, string? note, CancellationToken ct)
    {
        var env = commands.NewEnvelope(server, TimeSpan.FromMinutes(2));
        var wait = pending.ExpectIis(env.CommandId, server.Id);
        var sent = await commands.SendIisOpAsync(server, env, command);
        IisOpResult? r = null; string? err = sent.Error;
        if (sent.Ok)
        {
            try { r = await wait.WaitAsync(AgentTimeout, ct); err = r.Ok ? null : r.Error; }
            catch (TimeoutException) { pending.Forget(env.CommandId); err = "The agent did not answer in time."; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { err = "The agent did not answer in time."; }
        }
        else pending.Forget(env.CommandId);

        await audit.WriteAsync(actor, new AuditRecord(AuditAction(command), "IisObject", ObjectName(command), server.Id, server.EnvironmentName,
            Before: r?.Before, After: r?.After, Result: err is null ? "Success" : "Failed", Detail: err ?? note));
        if (err is not null) return new(false, err, false, null, r);

        if (r!.Changed) await RecordCreatedAsync(db, actor, server, command, r, projectId);
        await commands.SendDiscoveryAsync(server);               // refresh the stored inventory; the new item shows up in the onboarding list
        bus.Publish(new ServerChanged(server.Id, ServerChangeKind.Discovery));
        return new(true, null, false, null, r);
    }

    private static string? ObjectName(IisOpCommand c) => c.Op switch
    {
        IisOp.CreatePool or IisOp.GrantFolderAccess => c.PoolName,
        IisOp.CreateSite => c.SiteName,
        IisOp.CreateFolder => c.PhysicalPath,
        _ => $"site #{c.SiteId}",
    };

    private async Task RecordCreatedAsync(AppDbContext db, Actor actor, Server server, IisOpCommand c, IisOpResult r, Guid? projectId)
    {
        (IisItemKind kind, string key, string? path)? item = c.Op switch
        {
            IisOp.CreatePool => (IisItemKind.Pool, c.PoolName!, null),
            IisOp.CreateSite => (IisItemKind.Site, (r.SiteId ?? 0).ToString(), c.PhysicalPath),
            IisOp.CreateApplication => (IisItemKind.App, $"{c.SiteId}:{IisRules.NormalizeVirtualPath(c.AppPath!)}", c.PhysicalPath),
            IisOp.CreateVirtualDirectory => (IisItemKind.VDir, $"{c.SiteId}:{IisRules.NormalizeVirtualPath(c.VirtualPath!)}", c.PhysicalPath),
            IisOp.CreateFolder => (IisItemKind.Folder, c.PhysicalPath!, c.PhysicalPath),
            _ => null,
        };
        if (item is not { } i) return;
        db.IisCreatedItems.Add(new IisCreatedItem { Id = Guid.NewGuid(), ServerId = server.Id, Kind = i.kind, Key = i.key, Path = i.path, ProjectId = projectId, CreatedBy = actor.UserName, CreatedById = actor.UserId, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    // ======================= page =======================

    public async Task<IisPage?> GetPageAsync(Actor actor, Guid serverId, Guid? projectId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == serverId && !x.IsDeleted);
        if (s is null) return null;
        var operatorView = actor.Can(Permission.ViewServers, s.EnvironmentName);
        var ownerMode = false; Ownership? own = null;
        if (!operatorView)
        {
            if (projectId is not { } pid || !actor.Access.IsProjectOwner(pid)) return null;
            var comps = await db.Components.AsNoTracking().Where(c => c.ProjectId == pid).Select(c => c.Id).ToListAsync();
            if (!await db.DeploymentTargets.AnyAsync(t => comps.Contains(t.ComponentId) && t.ServerId == serverId)) return null;
            ownerMode = true;
        }
        else if (projectId is { } p2 && actor.Access.IsProjectOwner(p2) && !actor.Can(Permission.ManageIis, s.EnvironmentName)) ownerMode = true;
        if (ownerMode) own = await OwnershipAsync(db, serverId, projectId!.Value);

        var sites = await db.IisSites.AsNoTracking().Where(x => x.ServerId == serverId).Include(x => x.Bindings).Include(x => x.Applications).OrderBy(x => x.Name).ToListAsync();
        var linked = (await db.DeploymentTargets.AsNoTracking().Where(t => t.ServerId == serverId).Select(t => t.IisSiteId).ToListAsync()).ToHashSet();
        var rows = sites.Select(x => new IisSiteRow(x.IisSiteId, x.Name, x.State, x.PhysicalPath, x.AppPoolName,
            x.Bindings.Where(b => b.Protocol == "http").Select(b => new HttpBinding(b.Host ?? "", b.Port)).ToList(),
            x.Bindings.Where(b => b.Protocol != "http").Select(b => $"{b.Protocol}://{b.Host}:{b.Port}").ToList(),
            x.Applications.Where(a => a.Path != "/").Select(a => (a.Path, a.PhysicalPath, a.AppPoolName)).ToList(),
            own?.Sites.Contains(x.IisSiteId) ?? true, linked.Contains(x.IisSiteId))).ToList();
        var pools = await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).Select(p => p.Name).OrderBy(n => n).ToListAsync();
        if (own is not null) pools = pools.Where(n => own.Pools.Contains(n)).ToList();
        var canAct = ownerMode ? s.EnvironmentName != EnvironmentNames.Prod : actor.Can(Permission.ManageIis, s.EnvironmentName);
        var why = !canAct ? (ownerMode ? "In Production only a Server Operator or Platform Admin can change IIS." : "You can view this server but not change IIS. This needs the Server Operator role for this environment, or Platform Admin.") : WhyNot(s, commands.IsConnected(serverId));
        return new IisPage(s, rows, pools, canAct && why is null, why, ownerMode, projectId, !ownerMode && actor.Can(Permission.ManageIis, s.EnvironmentName));
    }
}
