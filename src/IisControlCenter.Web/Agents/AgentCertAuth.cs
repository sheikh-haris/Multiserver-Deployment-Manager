using System.Security.Claims;
using System.Text.Encodings.Web;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using IisControlCenter.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Agents;

public static class AgentCertAuth
{
    public const string Scheme = "AgentCert";
    public const string ServerIdClaim = "icc:server_id";
    public const string ThumbprintClaim = "icc:thumbprint";
}

/// <summary>
/// Accepts a client certificate only if its SHA-256 thumbprint is pinned in the database as Active.
/// The TLS layer accepts any certificate on purpose; trust comes from this pin, set when an admin approves registration.
/// </summary>
public sealed class AgentCertAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<AppDbContext> dbf,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cert = Context.Connection.ClientCertificate;
        if (cert is null) return AuthenticateResult.NoResult();

        var thumb = ServerService.Thumbprint(cert);
        var now = clock.GetUtcNow();
        if (cert.NotAfter.ToUniversalTime() <= now.UtcDateTime || cert.NotBefore.ToUniversalTime() > now.UtcDateTime.AddMinutes(10))
            return AuthenticateResult.Fail("Certificate outside validity period.");

        await using var db = await dbf.CreateDbContextAsync();
        var row = await db.AgentCertificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Thumbprint == thumb && c.Status == CertificateStatus.Active);
        if (row is null) return AuthenticateResult.Fail("Unknown or revoked certificate.");

        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == row.ServerId);
        if (server is null || server.AgentStatus == AgentStatus.Revoked) return AuthenticateResult.Fail("Server revoked.");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(AgentCertAuth.ServerIdClaim, row.ServerId.ToString()),
            new Claim(AgentCertAuth.ThumbprintClaim, thumb),
        }, AgentCertAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AgentCertAuth.Scheme));
    }
}
