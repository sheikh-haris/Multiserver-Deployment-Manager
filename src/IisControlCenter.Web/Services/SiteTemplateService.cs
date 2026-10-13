using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record SiteTemplateRow(Guid Id, string Name, string? Description, SiteTemplateDef Def, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Reusable site layouts. Managing templates needs the Server Operator or Platform Admin role. Applying one is the same as doing its steps by hand:
/// every step goes through the normal rules, audit and (in Production) the second-person approval, as ONE request.
/// </summary>
public sealed class SiteTemplateService(IDbContextFactory<AppDbContext> dbf, IisOpsService iis, AuditService audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static bool CanManage(Actor a) => a.Can(Permission.ManageIis);
    private static bool CanUse(Actor a) => CanManage(a) || a.Access.ActiveAssignments.Any(x => x.Role == Role.ProjectOwner);

    private static void RequireManage(Actor a)
    {
        if (!CanManage(a)) throw new ForbiddenException("Only a Server Operator or Platform Admin can manage site templates.");
    }

    public async Task<IReadOnlyList<SiteTemplateRow>> ListAsync(Actor actor)
    {
        if (!CanUse(actor)) return [];
        await using var db = await dbf.CreateDbContextAsync();
        var list = await db.SiteTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        return list.Select(t => new SiteTemplateRow(t.Id, t.Name, t.Description, JsonSerializer.Deserialize<SiteTemplateDef>(t.Json) ?? new SiteTemplateDef(), t.CreatedBy, t.CreatedAt)).ToList();
    }

    /// <summary>Reads the structure of an existing site from the last discovery. Only applications and virtual directories inside the site folder are captured.</summary>
    public async Task<OpResult<Guid>> CaptureAsync(Actor actor, Guid serverId, long iisSiteId, string name, string? description)
    {
        RequireManage(actor);
        if (TemplateRules.ValidateName(name) is { } ne) return OpResult<Guid>.Fail(ne);
        await using var db = await dbf.CreateDbContextAsync();
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId && !s.IsDeleted);
        if (server is null || !actor.Can(Permission.ManageIis, server.EnvironmentName)) return OpResult<Guid>.Fail("Server not found, or you cannot manage IIS on it.");
        var site = await db.IisSites.AsNoTracking().Where(s => s.ServerId == serverId && s.IisSiteId == iisSiteId)
            .Include(s => s.Applications).ThenInclude(a => a.VirtualDirs).FirstOrDefaultAsync();
        if (site is null || string.IsNullOrEmpty(site.PhysicalPath)) return OpResult<Guid>.Fail("Site not found in the last discovery.");

        static string? Rel(string root, string? p)
        {
            var r = root.Replace('\\', '/').TrimEnd('/'); var x = (p ?? "").Replace('\\', '/').TrimEnd('/');
            return x.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ? x[(r.Length + 1)..] : null;
        }
        var apps = new List<TemplateApp>(); var vdirs = new List<TemplateVDir>(); var skipped = 0;
        foreach (var a in site.Applications.OrderBy(a => a.Path))
        {
            if (a.Path == "/") { foreach (var v in a.VirtualDirs.Where(v => v.Path != "/")) { var rel = Rel(site.PhysicalPath, v.PhysicalPath); if (rel is null) skipped++; else vdirs.Add(new("/", v.Path, rel)); } continue; }
            var rf = Rel(site.PhysicalPath, a.PhysicalPath);
            if (rf is null) { skipped++; continue; }
            apps.Add(new(a.Path, rf));
            foreach (var v in a.VirtualDirs.Where(v => v.Path != "/")) { var rel = Rel(site.PhysicalPath, v.PhysicalPath); if (rel is null) skipped++; else vdirs.Add(new(a.Path, a.Path.TrimEnd('/') + v.Path, rel)); }
        }
        PoolConfig? pool = null;
        if (site.AppPoolName is { } pn && await db.IisAppPools.AsNoTracking().FirstOrDefaultAsync(p => p.ServerId == serverId && p.Name == pn) is { ConfigJson: not null } row)
            pool = JsonSerializer.Deserialize<PoolConfig>(row.ConfigJson) is { } c ? WithoutCustomIdentity(c) : null;
        var def = new SiteTemplateDef { Pool = pool, Apps = apps, VirtualDirectories = vdirs };
        var problems = TemplateRules.Validate(def);
        if (problems.Count > 0) return OpResult<Guid>.Fail("The site cannot be captured as a template: " + string.Join(" ", problems));
        var id = await SaveNewAsync(actor, db, name, description, def, $"captured from {site.Name} on {server.Name}" + (skipped > 0 ? $"; {skipped} item(s) outside the site folder were not captured" : ""));
        return id;
    }

    /// <summary>A pool that runs as a custom account or LocalSystem becomes ApplicationPoolIdentity in a template: a template never carries an account.</summary>
    private static PoolConfig WithoutCustomIdentity(PoolConfig c) => c with { IdentityType = PoolRules.Identities.Contains(c.IdentityType ?? "") ? c.IdentityType : null };

    public async Task<OpResult<Guid>> CreateAsync(Actor actor, string name, string? description, SiteTemplateDef def)
    {
        RequireManage(actor);
        if (TemplateRules.ValidateName(name) is { } ne) return OpResult<Guid>.Fail(ne);
        var problems = TemplateRules.Validate(def);
        if (problems.Count > 0) return OpResult<Guid>.Fail(string.Join(" ", problems));
        await using var db = await dbf.CreateDbContextAsync();
        return await SaveNewAsync(actor, db, name, description, def, "created");
    }

    private async Task<OpResult<Guid>> SaveNewAsync(Actor actor, AppDbContext db, string name, string? description, SiteTemplateDef def, string how)
    {
        name = name.Trim();
        if (await db.SiteTemplates.AnyAsync(t => t.Name == name)) return OpResult<Guid>.Fail($"A template named '{name}' already exists.");
        var t = new SiteTemplate { Id = Guid.NewGuid(), Name = name, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), Json = JsonSerializer.Serialize(def, Json), CreatedBy = actor.UserName, CreatedAt = clock.GetUtcNow() };
        db.SiteTemplates.Add(t);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Template.Create", "SiteTemplate", t.Id.ToString(), After: $"{name}: {how}"));
        return OpResult<Guid>.Success(t.Id);
    }

    public async Task<OpResult> UpdateAsync(Actor actor, Guid id, string name, string? description, SiteTemplateDef def)
    {
        RequireManage(actor);
        if (TemplateRules.ValidateName(name) is { } ne) return OpResult.Fail(ne);
        var problems = TemplateRules.Validate(def);
        if (problems.Count > 0) return OpResult.Fail(string.Join(" ", problems));
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.SiteTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return OpResult.Fail("Template not found.");
        name = name.Trim();
        if (await db.SiteTemplates.AnyAsync(x => x.Name == name && x.Id != id)) return OpResult.Fail($"A template named '{name}' already exists.");
        var before = t.Json;
        t.Name = name; t.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(); t.Json = JsonSerializer.Serialize(def, Json); t.UpdatedBy = actor.UserName; t.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Template.Update", "SiteTemplate", id.ToString(), Before: before, After: t.Json));
        return OpResult.Success();
    }

    /// <summary>Removes the TEMPLATE only. Sites that were created from it are not touched.</summary>
    public async Task<OpResult> RemoveAsync(Actor actor, Guid id)
    {
        RequireManage(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var t = await db.SiteTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return OpResult.Fail("Template not found.");
        db.SiteTemplates.Remove(t);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Template.Remove", "SiteTemplate", id.ToString(), Before: $"{t.Name}: {t.Json}"));
        return OpResult.Success();
    }

    public async Task<IisOpsService.BatchOutcome> ApplyAsync(Actor actor, Guid serverId, Guid templateId, TemplateApply input, Guid? projectId, string? reason, CancellationToken ct = default)
    {
        IisOpsService.BatchOutcome Fail(string e) => new(false, e, false, null, []);
        if (!CanUse(actor)) throw new ForbiddenException("You cannot use site templates.");
        if (IisRules.ValidateSiteName(input.SiteName) is { } sn) return Fail(sn);
        if (PoolRules.ValidateName(input.PoolName) is { } pn) return Fail(pn);
        var bp = IisRules.ValidateBindings(input.Bindings); if (bp.Count > 0) return Fail(string.Join(" ", bp));
        if (string.IsNullOrWhiteSpace(input.RootFolder)) return Fail("Enter the folder for the site.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var t = await db.SiteTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == templateId, ct);
        if (t is null) return Fail("Template not found.");
        var def = JsonSerializer.Deserialize<SiteTemplateDef>(t.Json) ?? new SiteTemplateDef();
        var plan = TemplatePlanner.Plan(def, input);
        var o = await iis.RunBatchAsync(actor, serverId, plan, projectId, reason, $"Apply template '{t.Name}' → site {input.SiteName}", ct);
        await audit.WriteAsync(actor, new AuditRecord("Template.Apply", "SiteTemplate", templateId.ToString(), serverId, null, After: $"{t.Name} → {input.SiteName} ({input.RootFolder})", Result: o.Ok ? "Success" : "Failed", Detail: o.Queued ? "Sent for approval" : o.Error));
        return o;
    }
}
