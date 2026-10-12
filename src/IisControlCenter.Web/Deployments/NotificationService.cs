using System.Net;
using System.Net.Mail;
using IisControlCenter.Core.Domain;
using IisControlCenter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IisControlCenter.Web.Deployments;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host)) throw new InvalidOperationException("No SMTP host is configured (Notifications:Smtp:Host).");
        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseSsl, Timeout = 30_000 };
        if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);
        using var msg = new MailMessage(o.From, to, subject, body) { IsBodyHtml = false };
        await client.SendMailAsync(msg, ct);
    }
}

/// <summary>
/// Email goes through a database outbox: a request never fails because the mail server is down, and nothing is lost on a restart.
/// Notifications carry no secrets: only names, status and a link.
/// </summary>
public sealed class NotificationService(IDbContextFactory<AppDbContext> dbf, IEmailSender sender, IOptions<DeploymentOptions> options, TimeProvider clock, ILogger<NotificationService> log)
{
    public const int MaxAttempts = 5;

    public string? Link(Guid requestId) =>
        string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl) ? null : $"{options.Value.PublicBaseUrl!.TrimEnd('/')}/deployments/{requestId}";

    public async Task EnqueueAsync(Guid? requestId, string kind, IEnumerable<string?> to, string subject, string body)
    {
        var addresses = to.Where(a => !string.IsNullOrWhiteSpace(a)).SelectMany(a => a!.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0) return;
        await using var db = await dbf.CreateDbContextAsync();
        foreach (var a in addresses)
            db.Notifications.Add(new Notification { Id = Guid.NewGuid(), RequestId = requestId, Kind = kind, ToAddress = a, Subject = subject, Body = body, Status = NotificationStatus.Pending, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    /// <summary>Sends what is waiting. Failed ones are retried up to <see cref="MaxAttempts"/> times.</summary>
    public async Task<int> SendPendingAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var list = await db.Notifications.Where(n => n.Status == NotificationStatus.Pending).OrderBy(n => n.CreatedAt).Take(50).ToListAsync(ct);
        var sent = 0;
        foreach (var n in list)
        {
            n.Attempts++;
            try { await sender.SendAsync(n.ToAddress, n.Subject, n.Body, ct); n.Status = NotificationStatus.Sent; n.SentAt = clock.GetUtcNow(); n.Error = null; sent++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (n.Attempts >= MaxAttempts) n.Status = NotificationStatus.Failed;
                log.LogWarning("Notification {Id} to {To} failed (attempt {N}): {Error}", n.Id, n.ToAddress, n.Attempts, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}
