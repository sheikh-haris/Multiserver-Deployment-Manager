using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class ServerServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    private async Task<Server> Load(Guid id) { await using var db = t.Db.CreateDbContext(); return await db.Servers.AsNoTracking().FirstAsync(s => s.Id == id); }
    private async Task<List<string>> AuditActions(string? result = null)
    {
        await using var db = t.Db.CreateDbContext();
        return await db.AuditLog.Where(a => result == null || a.Result == result).OrderBy(a => a.Id).Select(a => a.Action + ":" + a.Result).ToListAsync();
    }

    // ---------- create / permissions ----------

    [Fact]
    public async Task Admin_can_create_a_server_and_gets_a_one_time_token_valid_for_one_hour()
    {
        var r = await t.Servers.CreateAsync(t.Admin, "DEV-WEB01");
        Assert.True(r.Ok);
        Assert.Equal(t.Clock.GetUtcNow().AddHours(1), r.Value!.ExpiresAt);
        var s = await Load(r.Value.ServerId);
        Assert.Equal(AgentStatus.NotRegistered, s.AgentStatus);
        Assert.Null(s.EnvironmentName);
        Assert.Equal(ServerMode.ReadOnly, s.Mode);
    }

    [Fact]
    public async Task Token_is_stored_only_as_a_hash()
    {
        var r = await t.Servers.CreateAsync(t.Admin, "WEB01");
        await using var db = t.Db.CreateDbContext();
        var row = await db.RegistrationTokens.SingleAsync();
        Assert.NotEqual(r.Value!.Token, row.TokenHash);
        Assert.DoesNotContain(r.Value.Token, row.TokenHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Role.Auditor)]
    [InlineData(Role.ServerOperator)]
    public async Task Only_platform_admin_can_create_servers_and_the_denial_is_audited(Role? role)
    {
        var actor = t.ActorWith(role, "Dev");
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Servers.CreateAsync(actor, "X1"));
        Assert.Contains("Server.Create:Denied", await AuditActions());
    }

    [Fact]
    public async Task Duplicate_server_name_is_refused()
    {
        await t.Servers.CreateAsync(t.Admin, "WEB01");
        Assert.False((await t.Servers.CreateAsync(t.Admin, "WEB01")).Ok);
    }

    // ---------- registration ----------

    [Fact]
    public async Task Registration_pins_the_certificate_and_leaves_the_server_pending()
    {
        var (id, thumb) = await t.RegisteredServer();
        var s = await Load(id);
        Assert.Equal(AgentStatus.Pending, s.AgentStatus);
        Assert.Equal("Dev", s.DeclaredEnvironment);
        Assert.Null(s.EnvironmentName);                      // not approved yet
        Assert.True(await t.Servers.IsCertificateActiveAsync(id, thumb));
    }

    [Fact]
    public async Task Token_works_only_once()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var a = t.NewCert(); var b = t.NewCert();
        Assert.True((await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, a.Base64), null, a.Thumbprint)).Ok);
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, b.Base64), null, b.Thumbprint)).Ok);
        Assert.Contains("Agent.Register:Denied", await AuditActions());
    }

    [Fact]
    public async Task Token_expires_after_one_hour()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        t.Clock.Advance(TimeSpan.FromMinutes(61));
        var c = t.NewCert();
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, c.Base64), null, c.Thumbprint)).Ok);
    }

    [Fact]
    public async Task Unknown_token_gets_the_same_generic_message_as_an_expired_one()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var c = t.NewCert();
        var unknown = await t.Servers.RegisterAgentAsync(t.Request("nope", c.Base64), null, c.Thumbprint);
        t.Clock.Advance(TimeSpan.FromHours(2));
        var expired = await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, c.Base64), null, c.Thumbprint);
        Assert.Equal(unknown.Error, expired.Error);
    }

    [Fact]
    public async Task Registration_requires_proof_of_possession_of_the_certificate()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var registering = t.NewCert(); var other = t.NewCert();
        var r = await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, registering.Base64), null, other.Thumbprint);
        Assert.False(r.Ok);
        var none = await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, registering.Base64), null, null);
        Assert.False(none.Ok);
    }

    [Fact]
    public async Task Weak_or_odd_certificates_are_refused()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var small = t.NewCert(keyBits: 1024);
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, small.Base64), null, small.Thumbprint)).Ok);
        var tooLong = t.NewCert(lifetime: TimeSpan.FromDays(365 * 10));
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, tooLong.Base64), null, tooLong.Thumbprint)).Ok);
        var expired = t.NewCert(lifetime: TimeSpan.FromDays(1), notBefore: t.Clock.GetUtcNow().AddDays(-10));
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, expired.Base64), null, expired.Thumbprint)).Ok);
        // A failed attempt must not burn the token.
        var good = t.NewCert();
        Assert.True((await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, good.Base64), null, good.Thumbprint)).Ok);
    }

    [Fact]
    public async Task Certificate_with_a_private_key_is_refused()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var c = t.NewCert();
        var withKey = Convert.ToBase64String(c.Cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
        var r = await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, withKey), null, c.Thumbprint);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Unknown_environment_is_refused()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        var c = t.NewCert();
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value!.Token, c.Base64, env: "Moon"), null, c.Thumbprint)).Ok);
    }

    // ---------- approval ----------

    [Fact]
    public async Task Approval_must_match_the_environment_the_agent_declared()
    {
        var (id, _) = await t.RegisteredServer(env: "Dev");
        var r = await t.Servers.ApproveAsync(t.Admin, id, "Prod");
        Assert.False(r.Ok);
        Assert.Contains("fixed on the server", r.Error);
        Assert.Null((await Load(id)).EnvironmentName);
        Assert.Contains("Server.Approve:Denied", await AuditActions());
    }

    [Fact]
    public async Task Approved_server_starts_read_only_with_the_confirmed_environment()
    {
        var id = await t.ApprovedServer(env: "Staging");
        var s = await Load(id);
        Assert.Equal("Staging", s.EnvironmentName);
        Assert.Equal(ServerMode.ReadOnly, s.Mode);
        Assert.Equal(AgentStatus.Offline, s.AgentStatus);      // nothing connected in this test
        Assert.NotNull(s.ApprovedAt);
    }

    [Fact]
    public async Task Approval_asks_a_connected_agent_for_discovery()
    {
        var (id, _) = await t.RegisteredServer();
        t.Tracker.Add(id, "conn-1");
        Assert.True((await t.Servers.ApproveAsync(t.Admin, id, "Dev")).Ok);
        var cmd = Assert.Single(t.Hub.Client.Discoveries);
        Assert.Equal(id, cmd.ServerId);
        Assert.Equal("Dev", cmd.Environment);
        Assert.Equal(AgentStatus.Online, (await Load(id)).AgentStatus);
    }

    [Fact]
    public async Task Only_pending_servers_can_be_approved()
    {
        var id = await t.ApprovedServer();
        Assert.False((await t.Servers.ApproveAsync(t.Admin, id, "Dev")).Ok);
    }

    [Fact]
    public async Task Operator_cannot_approve()
    {
        var (id, _) = await t.RegisteredServer();
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Servers.ApproveAsync(t.ActorWith(Role.ServerOperator, "Dev"), id, "Dev"));
    }

    // ---------- mode ----------

    [Fact]
    public async Task Managed_mode_is_refused_unless_the_agent_was_installed_to_allow_it()
    {
        var id = await t.ApprovedServer();
        var r = await t.Servers.SetModeAsync(t.Admin, id, ServerMode.Managed);
        Assert.False(r.Ok);
        Assert.Equal(ServerMode.ReadOnly, (await Load(id)).Mode);
    }

    [Fact]
    public async Task Managed_mode_can_be_set_when_the_agent_allows_it_and_falls_back_if_it_stops_allowing()
    {
        var (id, thumb) = await t.RegisteredServer();
        Assert.Equal(ServerService.HelloOutcome.Pending, (await t.Servers.OnHelloAsync(id, Hello("Dev", allowManaged: true))).Outcome);
        Assert.True((await t.Servers.ApproveAsync(t.Admin, id, "Dev")).Ok);
        Assert.True((await t.Servers.SetModeAsync(t.Admin, id, ServerMode.Managed)).Ok);
        Assert.Equal(ServerMode.Managed, (await Load(id)).Mode);

        await t.Servers.OnHelloAsync(id, Hello("Dev", allowManaged: false));   // installer re-run without -AllowManaged
        Assert.Equal(ServerMode.ReadOnly, (await Load(id)).Mode);
    }

    // ---------- hello / heartbeat ----------

    private static AgentHello Hello(string env, bool allowManaged = false, int version = ContractInfo.Version) =>
        new(version, "0.1.0", "WEB01", "Windows", env, allowManaged, ["d:\\websites"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Hello_with_a_different_environment_than_registered_is_rejected_and_audited()
    {
        var id = await t.ApprovedServer(env: "Dev");
        var (outcome, _) = await t.Servers.OnHelloAsync(id, Hello("Prod"));
        Assert.Equal(ServerService.HelloOutcome.Rejected, outcome);
        Assert.Contains("Agent.Hello:Denied", await AuditActions());
    }

    [Fact]
    public async Task Hello_from_an_approved_agent_makes_it_online_and_records_its_local_limits()
    {
        var id = await t.ApprovedServer();
        var (outcome, _) = await t.Servers.OnHelloAsync(id, Hello("Dev"));
        Assert.Equal(ServerService.HelloOutcome.Accepted, outcome);
        var s = await Load(id);
        Assert.Equal(AgentStatus.Online, s.AgentStatus);
        Assert.Contains("websites", s.AllowedRoots);
    }

    [Fact]
    public async Task Hello_from_a_pending_agent_keeps_it_pending()
    {
        var (id, _) = await t.RegisteredServer();
        var (outcome, _) = await t.Servers.OnHelloAsync(id, Hello("Dev"));
        Assert.Equal(ServerService.HelloOutcome.Pending, outcome);
        Assert.Equal(AgentStatus.Pending, (await Load(id)).AgentStatus);
    }

    [Fact]
    public async Task Hello_from_an_old_contract_version_is_rejected()
    {
        var id = await t.ApprovedServer();
        Assert.Equal(ServerService.HelloOutcome.Rejected, (await t.Servers.OnHelloAsync(id, Hello("Dev", version: 0))).Outcome);
    }

    [Fact]
    public async Task Agent_goes_offline_after_90_seconds_without_a_heartbeat_and_back_online_on_the_next_one()
    {
        var id = await t.ApprovedServer();
        await t.Servers.OnHelloAsync(id, Hello("Dev"));
        t.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, await t.Servers.MarkStaleOfflineAsync(TimeSpan.FromSeconds(90)));
        t.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, await t.Servers.MarkStaleOfflineAsync(TimeSpan.FromSeconds(90)));
        Assert.Equal(AgentStatus.Offline, (await Load(id)).AgentStatus);

        await t.Servers.OnHeartbeatAsync(id, new Heartbeat(DateTimeOffset.UtcNow, true));
        Assert.Equal(AgentStatus.Online, (await Load(id)).AgentStatus);
    }

    // ---------- revoke / remove ----------

    [Fact]
    public async Task Revoking_stops_the_certificate_at_once_and_tells_the_agent()
    {
        var (id, thumb) = await t.RegisteredServer();
        t.Tracker.Add(id, "c1");
        Assert.True((await t.Servers.RevokeAsync(t.Admin, id, "lost server")).Ok);
        Assert.False(await t.Servers.IsCertificateActiveAsync(id, thumb));
        Assert.Equal(AgentStatus.Revoked, (await Load(id)).AgentStatus);
        Assert.Equal(["Revoked"], t.Hub.Client.Disconnects);
    }

    [Fact]
    public async Task Revoke_needs_a_reason()
    {
        var (id, _) = await t.RegisteredServer();
        Assert.False((await t.Servers.RevokeAsync(t.Admin, id, "  ")).Ok);
    }

    [Fact]
    public async Task Revoked_server_can_register_again_only_with_a_new_token_and_returns_to_unapproved()
    {
        var id = await t.ApprovedServer();
        await t.Servers.RevokeAsync(t.Admin, id, "rebuild");
        var token = await t.Servers.ReissueTokenAsync(t.Admin, id);
        Assert.True(token.Ok);
        var s = await Load(id);
        Assert.Equal(AgentStatus.NotRegistered, s.AgentStatus);
        Assert.Null(s.EnvironmentName);                      // must be approved again
        var c = t.NewCert();
        Assert.True((await t.Servers.RegisterAgentAsync(t.Request(token.Value!.Token, c.Base64), null, c.Thumbprint)).Ok);
    }

    [Fact]
    public async Task Reissuing_a_token_cancels_the_old_one()
    {
        var created = await t.Servers.CreateAsync(t.Admin, "WEB01");
        await t.Servers.ReissueTokenAsync(t.Admin, created.Value!.ServerId);
        var c = t.NewCert();
        Assert.False((await t.Servers.RegisterAgentAsync(t.Request(created.Value.Token, c.Base64), null, c.Thumbprint)).Ok);
    }

    [Fact]
    public async Task Removed_server_disappears_and_its_certificate_stops_working()
    {
        var (id, thumb) = await t.RegisteredServer();
        Assert.True((await t.Servers.RemoveAsync(t.Admin, id)).Ok);
        Assert.False(await t.Servers.IsCertificateActiveAsync(id, thumb));
        var (rows, total) = await t.Queries.ListAsync(t.Admin, null, null, null, null, false, 0, 50);
        Assert.Equal(0, total);
    }

    // ---------- discovery ----------

    private static DiscoveryResult Result(Guid cmd, params string[] siteNames) => new(cmd, DateTimeOffset.UtcNow, true,
        siteNames.Select((n, i) => new DiscoveredSite(i + 1, n, "Started", @"D:\Websites\" + n, "Pool" + n,
            [new DiscoveredBinding("http", "*", 80, n + ".local", null)],
            [new DiscoveredApplication("/", @"D:\Websites\" + n, "Pool" + n, [new DiscoveredVirtualDirectory("/", @"D:\Websites\" + n)])])).ToList(),
        siteNames.Select(n => new DiscoveredAppPool("Pool" + n, "ApplicationPoolIdentity", "v4.0", "Integrated", "Started")).ToList(),
        [new DiscoveredCertificate("AB12", "CN=x", DateTimeOffset.UtcNow.AddDays(10), "My")],
        [new DiscoveredDotNetVersion(".NET Framework", "4.8")]);

    [Fact]
    public async Task Discovery_is_stored_and_replaced_on_the_next_run()
    {
        var id = await t.ApprovedServer();
        Assert.True((await t.Servers.StoreDiscoveryAsync(id, Result(Guid.NewGuid(), "a", "b"))).Ok);
        var inv = await t.Queries.GetInventoryAsync(t.Admin, id);
        Assert.Equal(["a", "b"], inv.Sites.Select(s => s.Name));
        Assert.Single(inv.Sites[0].Bindings);
        Assert.Single(inv.Sites[0].Applications[0].VirtualDirs);

        await t.Servers.StoreDiscoveryAsync(id, Result(Guid.NewGuid(), "b", "c"));
        inv = await t.Queries.GetInventoryAsync(t.Admin, id);
        Assert.Equal(["b", "c"], inv.Sites.Select(s => s.Name));
        Assert.Equal(2, inv.Pools.Count);
        Assert.NotNull((await Load(id)).LastDiscoveryAt);
    }

    [Fact]
    public async Task Existing_mapping_status_survives_a_new_discovery()
    {
        var id = await t.ApprovedServer();
        await t.Servers.StoreDiscoveryAsync(id, Result(Guid.NewGuid(), "a"));
        await using (var db = t.Db.CreateDbContext())
        {
            (await db.IisSites.SingleAsync()).MappingStatus = MappingStatus.Mapped;
            await db.SaveChangesAsync();
        }
        await t.Servers.StoreDiscoveryAsync(id, Result(Guid.NewGuid(), "a"));
        Assert.Equal(MappingStatus.Mapped, (await t.Queries.GetInventoryAsync(t.Admin, id)).Sites.Single().MappingStatus);
    }

    [Fact]
    public async Task Discovery_from_an_unapproved_server_is_refused()
    {
        var (id, _) = await t.RegisteredServer();
        Assert.False((await t.Servers.StoreDiscoveryAsync(id, Result(Guid.NewGuid(), "a"))).Ok);
    }

    [Fact]
    public async Task Oversized_discovery_is_refused()
    {
        var id = await t.ApprovedServer();
        var big = Result(Guid.NewGuid(), Enumerable.Range(0, 5001).Select(i => "s" + i).ToArray());
        Assert.False((await t.Servers.StoreDiscoveryAsync(id, big)).Ok);
    }

    [Fact]
    public async Task Overlong_values_are_trimmed_instead_of_failing_the_whole_discovery()
    {
        var id = await t.ApprovedServer();
        var r = Result(Guid.NewGuid(), new string('x', 400));
        Assert.True((await t.Servers.StoreDiscoveryAsync(id, r)).Ok);
        Assert.Equal(300, (await t.Queries.GetInventoryAsync(t.Admin, id)).Sites.Single().Name.Length);
    }

    // ---------- command service ----------

    [Fact]
    public async Task Discovery_results_nobody_asked_for_are_not_accepted()
    {
        var id = await t.ApprovedServer();
        Assert.False(t.Commands.ConsumeIssued(Guid.NewGuid(), id));
        t.Tracker.Add(id, "c1");
        Assert.True((await t.Commands.RequestDiscoveryAsync(t.Admin, id)).Ok);
        var cmd = t.Hub.Client.Discoveries.Single();
        Assert.False(t.Commands.ConsumeIssued(cmd.CommandId, Guid.NewGuid()));   // wrong server
        Assert.True(t.Commands.ConsumeIssued(cmd.CommandId, id));
        Assert.False(t.Commands.ConsumeIssued(cmd.CommandId, id));               // only once
    }

    [Fact]
    public async Task Commands_carry_the_server_environment_mode_and_a_short_expiry()
    {
        var id = await t.ApprovedServer(env: "Staging");
        t.Tracker.Add(id, "c1");
        await t.Commands.RequestDiscoveryAsync(t.Admin, id);
        var c = t.Hub.Client.Discoveries.Single();
        Assert.Equal("Staging", c.Environment);
        Assert.Equal(ServerMode.ReadOnly, c.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), c.ExpiresAt - c.IssuedAt);
    }

    [Fact]
    public async Task Discovery_cannot_be_requested_from_an_offline_or_unapproved_agent()
    {
        var id = await t.ApprovedServer();
        Assert.False((await t.Commands.RequestDiscoveryAsync(t.Admin, id)).Ok);   // not connected
        var (pending, _) = await t.RegisteredServer("WEB02");
        t.Tracker.Add(pending, "c2");
        Assert.False((await t.Commands.RequestDiscoveryAsync(t.Admin, pending)).Ok);
    }

    [Fact]
    public async Task Operator_can_run_discovery_only_in_their_environment()
    {
        var dev = await t.ApprovedServer("DEV1", "Dev");
        var prod = await t.ApprovedServer("PRD1", "Prod");
        t.Tracker.Add(dev, "c1"); t.Tracker.Add(prod, "c2");
        var op = t.ActorWith(Role.ServerOperator, "Dev");
        Assert.True((await t.Commands.RequestDiscoveryAsync(op, dev)).Ok);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Commands.RequestDiscoveryAsync(op, prod));
    }

    // ---------- queries / scoping ----------

    [Fact]
    public async Task Operator_only_sees_servers_in_their_environment_and_never_pending_ones()
    {
        await t.ApprovedServer("DEV1", "Dev");
        await t.ApprovedServer("PRD1", "Prod");
        await t.RegisteredServer("NEW1");
        var op = t.ActorWith(Role.ServerOperator, "Dev");
        var (rows, total) = await t.Queries.ListAsync(op, null, null, null, null, false, 0, 50);
        Assert.Equal(["DEV1"], rows.Select(r => r.Name));
        Assert.Equal(1, total);
        var (all, _) = await t.Queries.ListAsync(t.Admin, null, null, null, null, false, 0, 50);
        Assert.Equal(3, all.Count);
        Assert.Null(await t.Queries.GetAsync(op, all.Single(r => r.Name == "PRD1").Id));
    }

    [Fact]
    public async Task Listing_supports_search_filter_sort_and_paging()
    {
        for (var i = 1; i <= 5; i++) await t.ApprovedServer("WEB0" + i, i % 2 == 0 ? "Prod" : "Dev");
        var (page, total) = await t.Queries.ListAsync(t.Admin, null, null, null, "Name", true, 0, 2);
        Assert.Equal(5, total);
        Assert.Equal(["WEB05", "WEB04"], page.Select(r => r.Name));
        Assert.Equal(2, (await t.Queries.ListAsync(t.Admin, null, "Prod", null, null, false, 0, 10)).Total);
        Assert.Single((await t.Queries.ListAsync(t.Admin, "WEB03", null, null, null, false, 0, 10)).Rows);
        Assert.Equal(5, (await t.Queries.ListAsync(t.Admin, null, null, AgentStatus.Offline, null, false, 0, 10)).Total);
    }

    [Fact]
    public async Task Users_without_a_role_cannot_list_servers()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Queries.ListAsync(t.Nobody, null, null, null, null, false, 0, 10));
    }

    [Fact]
    public async Task Changes_are_published_for_live_pages()
    {
        t.Events.Clear();
        var (id, _) = await t.RegisteredServer();
        Assert.Contains(t.Events, e => e.ServerId == id);
    }
}
