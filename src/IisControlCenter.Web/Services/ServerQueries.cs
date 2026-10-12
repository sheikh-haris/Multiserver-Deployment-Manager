using IisControlCenter.Core.Discovery;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record ServerRow(Guid Id, string Name, string? Hostname, string? EnvironmentName, string? DeclaredEnvironment,
    string Mode, AgentStatus AgentStatus, DateTimeOffset? LastSeenAt, DateTimeOffset? LastDiscoveryAt, string? AgentVersion, bool IsDomainJoined);

public sealed record DashboardData(int Total, int Pending, int Online, int Offline,
    IReadOnlyList<ServerRow> OfflineServers, IReadOnlyList<ReportCert> ExpiringCerts);

public sealed class ServerQueries(IDbContextFactory<AppDbContext> dbf, TimeProvider clock)
{
    private static IQueryable<Server> Scope(IQueryable<Server> q, Actor a)
    {
        var allowed = a.Access.AllowedEnvironments(Permission.ViewServers);
        if (allowed is null) return q;                       // unscoped: sees everything including pending servers
        var list = allowed.ToList();
        return q.Where(s => s.EnvironmentName != null && list.Contains(s.EnvironmentName));
    }

    private static ServerRow ToRow(Server s) => new(s.Id, s.Name, s.Hostname, s.EnvironmentName, s.DeclaredEnvironment,
        s.Mode.ToString(), s.AgentStatus, s.LastSeenAt, s.LastDiscoveryAt, s.AgentVersion, s.IsDomainJoined);

    public async Task<(IReadOnlyList<ServerRow> Rows, int Total)> ListAsync(
        Actor actor, string? search, string? environment, AgentStatus? status, string? sortBy, bool descending, int page, int pageSize)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var q = Scope(db.Servers.AsNoTracking(), actor);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var t = search.Trim();
            q = q.Where(s => s.Name.Contains(t) || (s.Hostname != null && s.Hostname.Contains(t)));
        }
        if (!string.IsNullOrEmpty(environment)) q = q.Where(s => s.EnvironmentName == environment);
        if (status is not null) q = q.Where(s => s.AgentStatus == status);

        var total = await q.CountAsync();
        q = (sortBy, descending) switch
        {
            ("Environment", false) => q.OrderBy(s => s.EnvironmentName).ThenBy(s => s.Name),
            ("Environment", true) => q.OrderByDescending(s => s.EnvironmentName).ThenBy(s => s.Name),
            ("LastSeenAt", false) => q.OrderBy(s => s.LastSeenAt),
            ("LastSeenAt", true) => q.OrderByDescending(s => s.LastSeenAt),
            ("Name", true) => q.OrderByDescending(s => s.Name),
            _ => q.OrderBy(s => s.Name),
        };
        var rows = await q.Skip(page * pageSize).Take(pageSize).ToListAsync();
        return (rows.Select(ToRow).ToList(), total);
    }

    public async Task<Server?> GetAsync(Actor actor, Guid id)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        return await Scope(db.Servers.AsNoTracking(), actor).FirstOrDefaultAsync(s => s.Id == id);
    }

    public sealed record Inventory(
        IReadOnlyList<IisSite> Sites, IReadOnlyList<IisAppPool> Pools,
        IReadOnlyList<IisCertificate> Certs, IReadOnlyList<ServerDotNetVersion> DotNet);

    public async Task<Inventory> GetInventoryAsync(Actor actor, Guid serverId)
    {
        if (await GetAsync(actor, serverId) is null) return new Inventory([], [], [], []);
        await using var db = await dbf.CreateDbContextAsync();
        var sites = await db.IisSites.AsNoTracking().Where(s => s.ServerId == serverId)
            .Include(s => s.Bindings).Include(s => s.Applications).ThenInclude(a => a.VirtualDirs)
            .AsSplitQuery().OrderBy(s => s.Name).ToListAsync();
        return new Inventory(sites,
            await db.IisAppPools.AsNoTracking().Where(p => p.ServerId == serverId).OrderBy(p => p.Name).ToListAsync(),
            await db.IisCertificates.AsNoTracking().Where(c => c.ServerId == serverId).OrderBy(c => c.NotAfter).ToListAsync(),
            await db.ServerDotNetVersions.AsNoTracking().Where(v => v.ServerId == serverId).OrderBy(v => v.Kind).ThenBy(v => v.Version).ToListAsync());
    }

    public async Task<(DiscoveryReport Report, IReadOnlyDictionary<Guid, string> ServerNames)> GetReportAsync(Actor actor)
    {
        if (!actor.Can(Permission.ViewDiscoveryReport)) throw new ForbiddenException("You do not have permission to view the discovery report.");
        await using var db = await dbf.CreateDbContextAsync();
        var servers = await Scope(db.Servers.AsNoTracking(), actor).Where(s => s.EnvironmentName != null).ToListAsync();
        var ids = servers.Select(s => s.Id).ToList();
        var sites = await db.IisSites.AsNoTracking().Where(s => ids.Contains(s.ServerId)).Include(s => s.Applications).AsSplitQuery().ToListAsync();
        var pools = await db.IisAppPools.AsNoTracking().Where(p => ids.Contains(p.ServerId)).ToListAsync();
        var certs = await db.IisCertificates.AsNoTracking().Where(c => ids.Contains(c.ServerId)).ToListAsync();
        var report = DiscoveryReportBuilder.Build(sites, pools, certs, clock.GetUtcNow(), 30);
        return (report, servers.ToDictionary(s => s.Id, s => s.Name));
    }

    public async Task<DashboardData> GetDashboardAsync(Actor actor)
    {
        Require(actor);
        await using var db = await dbf.CreateDbContextAsync();
        var servers = await Scope(db.Servers.AsNoTracking(), actor).ToListAsync();
        var offline = servers.Where(s => s.AgentStatus == AgentStatus.Offline).Select(ToRow).ToList();
        IReadOnlyList<ReportCert> certs = [];
        if (actor.Can(Permission.ViewDiscoveryReport))
            certs = (await GetReportAsync(actor)).Report.ExpiringCertificates.Take(10).ToList();
        return new DashboardData(servers.Count,
            servers.Count(s => s.AgentStatus == AgentStatus.Pending),
            servers.Count(s => s.AgentStatus == AgentStatus.Online),
            offline.Count, offline, certs);
    }

    private static void Require(Actor a)
    {
        if (!a.Can(Permission.ViewServers)) throw new ForbiddenException("You do not have permission to view servers.");
    }
}
