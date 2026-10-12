using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using IisControlCenter.Core.Security;
using IisControlCenter.Data;
using IisControlCenter.Web.Agents;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Web.Services;

public sealed record NewServerResult(Guid ServerId, string Token, DateTimeOffset ExpiresAt);

public sealed class ServerService(
    IDbContextFactory<AppDbContext> dbf,
    AuditService audit,
    AgentConnectionTracker tracker,
    AgentCommandService commands,
    ServerEventBus bus,
    TimeProvider clock)
{
    public const int DefaultTokenMinutes = 60;

    // ---------------- Admin actions ----------------

    public async Task<OpResult<NewServerResult>> CreateAsync(Actor actor, string name)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.Create", null);
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 200) return OpResult<NewServerResult>.Fail("Name must be 2 to 200 characters.");

        await using var db = await dbf.CreateDbContextAsync();
        if (await db.Servers.AnyAsync(s => s.Name == name))
            return OpResult<NewServerResult>.Fail("A server with this name already exists.");

        var now = clock.GetUtcNow();
        var server = new Server { Id = Guid.NewGuid(), Name = name, CreatedAt = now, CreatedBy = actor.UserName };
        db.Servers.Add(server);
        var (token, expires) = AddToken(db, server.Id, actor.UserName, now);
        await db.SaveChangesAsync();

        await audit.WriteAsync(actor, new AuditRecord("Server.Create", "Server", server.Id.ToString(), server.Id, After: name));
        bus.Publish(new ServerChanged(server.Id, ServerChangeKind.List));
        return OpResult<NewServerResult>.Success(new NewServerResult(server.Id, token, expires));
    }

    /// <summary>New one-time token for a server that has not registered, or was revoked.</summary>
    public async Task<OpResult<NewServerResult>> ReissueTokenAsync(Actor actor, Guid serverId)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.ReissueToken", serverId);
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult<NewServerResult>.Fail("Server not found.");
        if (s.AgentStatus is not (AgentStatus.NotRegistered or AgentStatus.Revoked))
            return OpResult<NewServerResult>.Fail("Only servers that are not registered, or were revoked, can get a new token.");

        var now = clock.GetUtcNow();
        // Old unused tokens stop working.
        var old = await db.RegistrationTokens.Where(t => t.ServerId == serverId && t.UsedAt == null).ToListAsync();
        foreach (var t in old) t.UsedAt = now;
        if (s.AgentStatus == AgentStatus.Revoked)
        {
            s.AgentStatus = AgentStatus.NotRegistered;
            s.EnvironmentName = null;
            s.DeclaredEnvironment = null;
            s.Mode = ServerMode.ReadOnly;
            s.ApprovedBy = null; s.ApprovedAt = null;
        }
        var (token, expires) = AddToken(db, serverId, actor.UserName, now);
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Server.ReissueToken", "Server", serverId.ToString(), serverId));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        return OpResult<NewServerResult>.Success(new NewServerResult(serverId, token, expires));
    }

    public async Task<OpResult> ApproveAsync(Actor actor, Guid serverId, string environment)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.Approve", serverId);
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult.Fail("Server not found.");
        if (s.AgentStatus != AgentStatus.Pending) return OpResult.Fail("Only pending servers can be approved.");
        if (!await db.Environments.AnyAsync(e => e.Name == environment)) return OpResult.Fail("Unknown environment.");

        if (!string.Equals(s.DeclaredEnvironment, environment, StringComparison.OrdinalIgnoreCase))
        {
            await audit.WriteAsync(actor, new AuditRecord("Server.Approve", "Server", serverId.ToString(), serverId, environment,
                Result: "Denied", Detail: $"Environment mismatch. Agent declared '{s.DeclaredEnvironment}'."));
            return OpResult.Fail(
                $"The agent was installed as '{s.DeclaredEnvironment}'. That is fixed on the server itself. " +
                "To use a different environment, re-run the installer on the server with the right one.");
        }

        var before = Snapshot(s);
        s.EnvironmentName = db.Environments.Local.FirstOrDefault(e => e.Name == environment)?.Name
                            ?? (await db.Environments.FirstAsync(e => e.Name == environment)).Name;
        s.Mode = ServerMode.ReadOnly;           // always starts read-only
        s.ApprovedBy = actor.UserName;
        s.ApprovedAt = clock.GetUtcNow();
        s.AgentStatus = tracker.IsConnected(s.Id) ? AgentStatus.Online : AgentStatus.Offline;
        await db.SaveChangesAsync();

        await audit.WriteAsync(actor, new AuditRecord("Server.Approve", "Server", serverId.ToString(), serverId, s.EnvironmentName, before, Snapshot(s)));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        if (s.AgentStatus == AgentStatus.Online) await commands.SendDiscoveryAsync(s);
        return OpResult.Success();
    }

    public async Task<OpResult> SetModeAsync(Actor actor, Guid serverId, ServerMode mode)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.SetMode", serverId);
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult.Fail("Server not found.");
        if (s.EnvironmentName is null) return OpResult.Fail("Approve the server first.");
        if (mode == ServerMode.Managed && !s.LocalAllowManaged)
            return OpResult.Fail("This agent was installed without permission for Managed mode. " +
                                 "Re-run the installer on the server with -AllowManaged if you want it.");
        var before = Snapshot(s);
        s.Mode = mode;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Server.SetMode", "Server", serverId.ToString(), serverId, s.EnvironmentName, before, Snapshot(s)));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        return OpResult.Success();
    }

    /// <summary>Rejects a pending server, or revokes an approved one. Certificates stop working at once.</summary>
    public async Task<OpResult> RevokeAsync(Actor actor, Guid serverId, string reason)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.Revoke", serverId);
        if (string.IsNullOrWhiteSpace(reason)) return OpResult.Fail("A reason is required.");
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult.Fail("Server not found.");
        await RevokeCoreAsync(db, s, actor.UserName, reason.Trim());
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Server.Revoke", "Server", serverId.ToString(), serverId, s.EnvironmentName, Detail: reason));
        await commands.DisconnectAsync(serverId, "Revoked");
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        return OpResult.Success();
    }

    public async Task<OpResult> RemoveAsync(Actor actor, Guid serverId)
    {
        await RequireAsync(actor, Permission.ManageServers, "Server.Remove", serverId);
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return OpResult.Fail("Server not found.");
        await RevokeCoreAsync(db, s, actor.UserName, "Server removed");
        s.IsDeleted = true;
        await db.SaveChangesAsync();
        await audit.WriteAsync(actor, new AuditRecord("Server.Remove", "Server", serverId.ToString(), serverId, s.EnvironmentName, Before: s.Name));
        await commands.DisconnectAsync(serverId, "Removed");
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.List));
        return OpResult.Success();
    }

    // ---------------- Agent registration ----------------

    public async Task<OpResult<RegisterResponse>> RegisterAgentAsync(RegisterRequest req, string? ip, string? presentedThumbprint)
    {
        const string generic = "Registration failed. The token is invalid or expired.";
        if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.CertificateBase64))
            return OpResult<RegisterResponse>.Fail(generic);

        await using var db = await dbf.CreateDbContextAsync();
        var hash = RegistrationTokens.Hash(req.Token.Trim());
        var now = clock.GetUtcNow();
        var token = await db.RegistrationTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
        {
            await audit.WriteSystemAsync(new AuditRecord("Agent.Register", "Server", token?.ServerId.ToString(), token?.ServerId,
                Result: "Denied", Detail: token is null ? "Unknown token" : token.UsedAt is not null ? "Token already used" : "Token expired"), ip);
            return OpResult<RegisterResponse>.Fail(generic);
        }

        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == token.ServerId);
        if (server is null) return OpResult<RegisterResponse>.Fail(generic);

        X509Certificate2 cert;
        try { cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(req.CertificateBase64)); }
        catch { return OpResult<RegisterResponse>.Fail("The certificate could not be read."); }
        using (cert)
        {
            var problem = ValidateCertificate(cert, now);
            if (problem is not null) return OpResult<RegisterResponse>.Fail(problem);

            if (!await db.Environments.AnyAsync(e => e.Name == req.DeclaredEnvironment))
                return OpResult<RegisterResponse>.Fail("Unknown environment declared by the agent.");

            var thumb = Thumbprint(cert);
            // Proof of possession: the TLS client certificate used for this call must be the one being registered.
            if (!string.Equals(presentedThumbprint, thumb, StringComparison.OrdinalIgnoreCase))
                return OpResult<RegisterResponse>.Fail("Connect with the certificate you are registering.");
            if (await db.AgentCertificates.AnyAsync(c => c.Thumbprint == thumb))
                return OpResult<RegisterResponse>.Fail("This certificate is already registered.");

            token.UsedAt = now;
            foreach (var old in await db.AgentCertificates.Where(c => c.ServerId == server.Id && c.Status == CertificateStatus.Active).ToListAsync())
                old.Status = CertificateStatus.Superseded;

            db.AgentCertificates.Add(new AgentCertificate
            {
                Id = Guid.NewGuid(), ServerId = server.Id, Thumbprint = thumb, NotAfter = cert.NotAfter.ToUniversalTime(),
                Status = CertificateStatus.Active, CreatedAt = now,
            });
            server.Hostname = Trunc(req.Hostname, 255);
            server.DeclaredEnvironment = req.DeclaredEnvironment;
            server.OsVersion = Trunc(req.OsVersion, 200);
            server.AgentVersion = Trunc(req.AgentVersion, 50);
            server.IsDomainJoined = req.IsDomainJoined;
            server.AgentStatus = AgentStatus.Pending;
            await db.SaveChangesAsync();

            await audit.WriteSystemAsync(new AuditRecord("Agent.Register", "Server", server.Id.ToString(), server.Id,
                After: $"host={server.Hostname}; declared={req.DeclaredEnvironment}; cert={thumb[..16]}…"), ip);
            bus.Publish(new ServerChanged(server.Id, ServerChangeKind.Status));
            return OpResult<RegisterResponse>.Success(new RegisterResponse(server.Id, thumb));
        }
    }

    public static string Thumbprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    public static string? ValidateCertificate(X509Certificate2 cert, DateTimeOffset now)
    {
        if (cert.HasPrivateKey) return "Send the public certificate only.";
        if (cert.NotBefore.ToUniversalTime() > now.UtcDateTime.AddMinutes(10)) return "The certificate is not valid yet.";
        if (cert.NotAfter.ToUniversalTime() <= now.UtcDateTime) return "The certificate has expired.";
        if (cert.NotAfter.ToUniversalTime() > now.UtcDateTime.AddYears(3)) return "The certificate lifetime is too long (max 3 years).";
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is not null) return rsa.KeySize >= 2048 ? null : "RSA key must be at least 2048 bits.";
        using var ec = cert.GetECDsaPublicKey();
        if (ec is not null) return ec.KeySize >= 256 ? null : "EC key must be at least 256 bits.";
        return "Unsupported key type.";
    }

    // ---------------- Agent hub events ----------------

    public enum HelloOutcome { Accepted, Pending, Rejected }

    public async Task<(HelloOutcome Outcome, string? Reason)> OnHelloAsync(Guid serverId, AgentHello hello)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null || s.AgentStatus == AgentStatus.Revoked) return (HelloOutcome.Rejected, "Server unknown or revoked.");

        if (hello.ContractVersion < ContractInfo.MinimumAgentVersion)
            return (HelloOutcome.Rejected, "Agent is too old. Update the agent.");

        // The agent's local config is the source of truth for its environment. A mismatch is never accepted.
        var expected = s.EnvironmentName ?? s.DeclaredEnvironment;
        if (!string.Equals(expected, hello.DeclaredEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            await audit.WriteSystemAsync(new AuditRecord("Agent.Hello", "Server", serverId.ToString(), serverId, s.EnvironmentName,
                Result: "Denied", Detail: $"Environment mismatch: expected '{expected}', agent says '{hello.DeclaredEnvironment}'."));
            return (HelloOutcome.Rejected, "Environment mismatch.");
        }

        s.AgentVersion = Trunc(hello.AgentVersion, 50);
        s.OsVersion = Trunc(hello.OsVersion, 200);
        s.Hostname = Trunc(hello.Hostname, 255);
        s.LocalAllowManaged = hello.LocalAllowManaged;
        s.AllowedRoots = Trunc(JsonSerializer.Serialize(hello.AllowedRoots), 2000);
        s.LastSeenAt = clock.GetUtcNow();
        // Local config no longer allows Managed -> fall back right away.
        if (!hello.LocalAllowManaged && s.Mode == ServerMode.Managed) s.Mode = ServerMode.ReadOnly;

        var approved = s.EnvironmentName is not null;
        s.AgentStatus = approved ? AgentStatus.Online : AgentStatus.Pending;
        await db.SaveChangesAsync();
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
        return (approved ? HelloOutcome.Accepted : HelloOutcome.Pending, null);
    }

    public async Task OnHeartbeatAsync(Guid serverId, Heartbeat hb)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null) return;
        var wasOffline = s.AgentStatus == AgentStatus.Offline;
        s.LastSeenAt = clock.GetUtcNow();
        s.IisInstalled = hb.IisInstalled;
        if (wasOffline && s.EnvironmentName is not null) s.AgentStatus = AgentStatus.Online;
        await db.SaveChangesAsync();
        if (wasOffline) bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
    }

    public async Task OnDisconnectedAsync(Guid serverId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null || s.AgentStatus is not AgentStatus.Online) return;
        s.AgentStatus = AgentStatus.Offline;
        await db.SaveChangesAsync();
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Status));
    }

    /// <summary>Marks agents Offline when no heartbeat arrived in time. Returns how many changed.</summary>
    public async Task<int> MarkStaleOfflineAsync(TimeSpan after)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var cutoff = clock.GetUtcNow() - after;
        var stale = await db.Servers.Where(s => s.AgentStatus == AgentStatus.Online && (s.LastSeenAt == null || s.LastSeenAt < cutoff)).ToListAsync();
        foreach (var s in stale) s.AgentStatus = AgentStatus.Offline;
        if (stale.Count == 0) return 0;
        await db.SaveChangesAsync();
        foreach (var s in stale) bus.Publish(new ServerChanged(s.Id, ServerChangeKind.Status));
        return stale.Count;
    }

    public async Task<bool> IsCertificateActiveAsync(Guid serverId, string thumbprint)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.AgentCertificates.AnyAsync(c => c.ServerId == serverId && c.Thumbprint == thumbprint
                                                         && c.Status == CertificateStatus.Active && c.NotAfter > clock.GetUtcNow())
               && await db.Servers.AnyAsync(s => s.Id == serverId && s.AgentStatus != AgentStatus.Revoked);
    }

    public async Task<OpResult> StoreDiscoveryAsync(Guid serverId, DiscoveryResult r)
    {
        const int maxSites = 5000;
        if (r.Sites.Count > maxSites || r.AppPools.Count > maxSites || r.Certificates.Count > maxSites)
            return OpResult.Fail("Discovery result is too large.");

        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.Servers.FirstOrDefaultAsync(x => x.Id == serverId);
        if (s is null || s.EnvironmentName is null) return OpResult.Fail("Server is not approved.");

        var now = clock.GetUtcNow();
        var oldSites = await db.IisSites.Where(x => x.ServerId == serverId).ToListAsync();
        var keepMapping = oldSites.ToDictionary(x => x.IisSiteId, x => x.MappingStatus);
        db.IisSites.RemoveRange(oldSites);
        db.IisAppPools.RemoveRange(await db.IisAppPools.Where(x => x.ServerId == serverId).ToListAsync());
        db.IisCertificates.RemoveRange(await db.IisCertificates.Where(x => x.ServerId == serverId).ToListAsync());
        db.ServerDotNetVersions.RemoveRange(await db.ServerDotNetVersions.Where(x => x.ServerId == serverId).ToListAsync());
        await db.SaveChangesAsync();

        foreach (var site in r.Sites.DistinctBy(x => x.IisSiteId))
        {
            db.IisSites.Add(new IisSite
            {
                Id = Guid.NewGuid(), ServerId = serverId, IisSiteId = site.IisSiteId,
                Name = Trunc(site.Name, 300)!, State = Trunc(site.State, 30)!,
                PhysicalPath = Trunc(site.PhysicalPath, 1000), AppPoolName = Trunc(site.AppPoolName, 300),
                MappingStatus = keepMapping.TryGetValue(site.IisSiteId, out var m) ? m : MappingStatus.Unassigned,
                DiscoveredAt = now,
                Bindings = site.Bindings.Take(200).Select(b => new IisBinding
                {
                    Id = Guid.NewGuid(), Protocol = Trunc(b.Protocol, 20)!, IpAddress = Trunc(b.IpAddress, 100),
                    Port = b.Port, Host = Trunc(b.Host, 500), CertThumbprint = Trunc(b.CertThumbprint, 64),
                }).ToList(),
                Applications = site.Applications.Take(500).Select(a => new IisApplication
                {
                    Id = Guid.NewGuid(), Path = Trunc(a.Path, 500)!, PhysicalPath = Trunc(a.PhysicalPath, 1000),
                    AppPoolName = Trunc(a.AppPoolName, 300),
                    VirtualDirs = a.VirtualDirectories.Take(500).Select(v => new IisVirtualDir
                    {
                        Id = Guid.NewGuid(), Path = Trunc(v.Path, 500)!, PhysicalPath = Trunc(v.PhysicalPath, 1000),
                    }).ToList(),
                }).ToList(),
            });
        }
        foreach (var p in r.AppPools.DistinctBy(x => x.Name))
            db.IisAppPools.Add(new IisAppPool
            {
                Id = Guid.NewGuid(), ServerId = serverId, Name = Trunc(p.Name, 300)!, IdentityType = Trunc(p.IdentityType, 50)!,
                RuntimeVersion = Trunc(p.RuntimeVersion, 30), PipelineMode = Trunc(p.PipelineMode, 30)!, State = Trunc(p.State, 30)!,
            });
        foreach (var c in r.Certificates)
            db.IisCertificates.Add(new IisCertificate
            {
                Id = Guid.NewGuid(), ServerId = serverId, Thumbprint = Trunc(c.Thumbprint, 64)!, Subject = Trunc(c.Subject, 1000)!,
                NotAfter = c.NotAfter, Store = Trunc(c.Store, 100)!,
            });
        foreach (var v in r.DotNetVersions)
            db.ServerDotNetVersions.Add(new ServerDotNetVersion { Id = Guid.NewGuid(), ServerId = serverId, Kind = Trunc(v.Kind, 50)!, Version = Trunc(v.Version, 50)! });

        s.LastDiscoveryAt = now;
        s.IisInstalled = r.IisInstalled;
        await db.SaveChangesAsync();

        await audit.WriteSystemAsync(new AuditRecord("Discovery.Received", "Server", serverId.ToString(), serverId, s.EnvironmentName,
            After: $"sites={r.Sites.Count}; pools={r.AppPools.Count}; certs={r.Certificates.Count}"));
        bus.Publish(new ServerChanged(serverId, ServerChangeKind.Discovery));
        return OpResult.Success();
    }

    // ---------------- helpers ----------------

    private async Task RequireAsync(Actor actor, Permission p, string action, Guid? serverId)
    {
        if (actor.Can(p)) return;
        await audit.WriteAsync(actor, new AuditRecord(action, "Server", serverId?.ToString(), serverId, Result: "Denied"));
        throw new ForbiddenException("You do not have permission to do this.");
    }

    private (string Token, DateTimeOffset Expires) AddToken(AppDbContext db, Guid serverId, string by, DateTimeOffset now)
    {
        var (token, hash) = RegistrationTokens.Create();
        var expires = now.AddMinutes(DefaultTokenMinutes);
        db.RegistrationTokens.Add(new RegistrationToken
        {
            Id = Guid.NewGuid(), ServerId = serverId, TokenHash = hash, ExpiresAt = expires, CreatedBy = by, CreatedAt = now,
        });
        return (token, expires);
    }

    private async Task RevokeCoreAsync(AppDbContext db, Server s, string by, string reason)
    {
        var now = clock.GetUtcNow();
        foreach (var c in await db.AgentCertificates.Where(c => c.ServerId == s.Id && c.Status == CertificateStatus.Active).ToListAsync())
        {
            c.Status = CertificateStatus.Revoked; c.RevokedAt = now; c.RevokedBy = by; c.RevokeReason = Trunc(reason, 500);
        }
        foreach (var t in await db.RegistrationTokens.Where(t => t.ServerId == s.Id && t.UsedAt == null).ToListAsync()) t.UsedAt = now;
        s.AgentStatus = AgentStatus.Revoked;
        s.Mode = ServerMode.ReadOnly;
    }

    private static string Snapshot(Server s) =>
        $"status={s.AgentStatus}; env={s.EnvironmentName}; mode={s.Mode}";

    private static string? Trunc(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];
}
