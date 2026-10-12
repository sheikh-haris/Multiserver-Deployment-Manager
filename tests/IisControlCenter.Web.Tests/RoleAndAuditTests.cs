using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Tests;

public class RoleServiceTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    [Fact]
    public async Task Individual_users_need_an_expiry_but_groups_do_not()
    {
        var user = await t.Roles.GrantAsync(t.Admin, PrincipalType.User, "oid-1", "Bob", Role.Auditor, null, null);
        Assert.False(user.Ok);
        Assert.True((await t.Roles.GrantAsync(t.Admin, PrincipalType.User, "oid-1", "Bob", Role.Auditor, null, t.Clock.GetUtcNow().AddDays(30))).Ok);
        Assert.True((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "grp-1", "Auditors", Role.Auditor, null, null)).Ok);
    }

    [Fact]
    public async Task Expiry_must_be_in_the_future()
    {
        Assert.False((await t.Roles.GrantAsync(t.Admin, PrincipalType.User, "oid-1", "Bob", Role.Auditor, null, t.Clock.GetUtcNow().AddMinutes(-1))).Ok);
    }

    [Fact]
    public async Task Server_operator_must_have_an_explicit_environment_choice()
    {
        Assert.False((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.ServerOperator, null, null)).Ok);
        Assert.True((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.ServerOperator, "Dev", null)).Ok);
        Assert.True((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g2", "G2", Role.ServerOperator, RoleService.AllEnvironments, null)).Ok);
        var list = await t.Roles.ListPlatformAsync(t.Admin);
        Assert.Null(list.Single(r => r.PrincipalId == "g2").EnvironmentName);          // "all" is stored as null
        Assert.Equal("Dev", list.Single(r => r.PrincipalId == "g").EnvironmentName);
    }

    [Fact]
    public async Task Environment_is_ignored_for_roles_that_are_not_environment_scoped()
    {
        await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.Auditor, "Dev", null);
        Assert.Null((await t.Roles.ListPlatformAsync(t.Admin)).Single().EnvironmentName);
    }

    [Fact]
    public async Task Project_roles_cannot_be_granted_as_platform_roles()
    {
        Assert.False((await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.Approver, null, null)).Ok);
    }

    [Fact]
    public async Task Grants_and_revokes_are_audited_and_revoked_roles_disappear()
    {
        await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.Auditor, null, null);
        var id = (await t.Roles.ListPlatformAsync(t.Admin)).Single().Id;
        Assert.True((await t.Roles.RevokeAsync(t.Admin, id)).Ok);
        Assert.Empty(await t.Roles.ListPlatformAsync(t.Admin));
        await using var db = t.Db.CreateDbContext();
        var actions = await db.AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("Role.Grant", actions);
        Assert.Contains("Role.Revoke", actions);
    }

    [Theory]
    [InlineData(Role.Auditor)]
    [InlineData(Role.ServerOperator)]
    public async Task Only_platform_admins_manage_roles(Role role)
    {
        var actor = t.ActorWith(role, "Dev");
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Roles.GrantAsync(actor, PrincipalType.Group, "g", "G", Role.PlatformAdmin, null, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Roles.ListPlatformAsync(actor));
        await using var db = t.Db.CreateDbContext();
        Assert.Contains(await db.AuditLog.ToListAsync(), a => a.Action == "Role.Grant" && a.Result == "Denied");
    }
}

public class AuditTests : IDisposable
{
    private readonly TestEnv t = new();
    public void Dispose() => t.Dispose();

    [Fact]
    public async Task Audit_entries_record_who_what_where_and_form_an_intact_chain()
    {
        await t.Servers.CreateAsync(t.Admin, "WEB01");
        await t.Servers.CreateAsync(t.Admin, "WEB02");
        await t.Roles.GrantAsync(t.Admin, PrincipalType.Group, "g", "G", Role.Auditor, null, null);
        Assert.Null(await t.AuditQueries.VerifyChainAsync(t.ActorWith(Role.Auditor)));

        var (rows, total) = await t.AuditQueries.ListAsync(t.ActorWith(Role.Auditor), new AuditFilter(null, null, null, null, null), 0, 50);
        Assert.Equal(3, total);
        Assert.Equal("Role.Grant", rows[0].Action);              // newest first
        Assert.Equal("10.0.0.1", rows[0].ClientIp);
        Assert.Equal("User u1", rows[0].UserName);
    }

    [Fact]
    public async Task Editing_a_row_directly_in_the_database_is_detected()
    {
        await t.Servers.CreateAsync(t.Admin, "WEB01");
        await t.Servers.CreateAsync(t.Admin, "WEB02");
        await using (var db = t.Db.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE AuditLog SET UserName = 'someone else' WHERE Id = 1");
        Assert.Equal(1, await t.AuditQueries.VerifyChainAsync(t.ActorWith(Role.Auditor)));
    }

    [Fact]
    public async Task Search_and_result_filters_work()
    {
        await t.Servers.CreateAsync(t.Admin, "WEB01");
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Servers.CreateAsync(t.Nobody, "X1"));
        var a = t.ActorWith(Role.Auditor);
        Assert.Single((await t.AuditQueries.ListAsync(a, new AuditFilter(null, null, "Denied", null, null), 0, 10)).Rows);
        Assert.Single((await t.AuditQueries.ListAsync(a, new AuditFilter("WEB01", null, null, null, null), 0, 10)).Rows);
    }

    [Fact]
    public async Task Only_people_with_audit_permission_can_read_or_export()
    {
        var op = t.ActorWith(Role.ServerOperator, "Dev");
        await Assert.ThrowsAsync<ForbiddenException>(() => t.AuditQueries.ListAsync(op, new AuditFilter(null, null, null, null, null), 0, 10));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.AuditQueries.ExportCsvAsync(op, new AuditFilter(null, null, null, null, null)));
    }

    [Fact]
    public async Task Exporting_is_itself_audited()
    {
        await t.Servers.CreateAsync(t.Admin, "WEB01");
        var csv = await t.AuditQueries.ExportCsvAsync(t.ActorWith(Role.Auditor), new AuditFilter(null, null, null, null, null));
        Assert.Contains("Server.Create", csv);
        var (rows, _) = await t.AuditQueries.ListAsync(t.ActorWith(Role.Auditor), new AuditFilter(null, "Audit.Export", null, null, null), 0, 10);
        Assert.Single(rows);
    }

    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+cmd", "\"'+cmd\"")]
    [InlineData("-5", "\"'-5\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "\"\"")]
    public void Csv_cells_are_quoted_and_formulas_are_neutralised(string? input, string expected) =>
        Assert.Equal(expected, AuditQueries.Csv(input));
}
