using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace IisControlCenter.Web.Auth;

/// <summary>
/// One HTTPS port serves two host names (decision D2).
/// The UI name never asks for a client certificate. The agents name requires one.
/// The certificate itself is NOT validated by TLS; trust comes from the thumbprint pinned in the database.
/// </summary>
public static class TlsSetup
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var agentsHost = cfg["Portal:AgentsHost"];
        var thumbprint = cfg["Portal:Tls:Thumbprint"];
        var pfx = cfg["Portal:Tls:PfxPath"];
        if (string.IsNullOrEmpty(thumbprint) && string.IsNullOrEmpty(pfx))
        {
            // Development: Kestrel default endpoints. Ask for (but do not require) client certificates so agents can be tried locally.
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(o =>
            {
                o.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                o.AllowAnyClientCertificate();
            }));
            return;
        }

        var server = LoadServerCertificate(thumbprint, pfx);
        var port = cfg.GetValue("Portal:HttpsPort", 443);
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port, lo =>
        {
            lo.Protocols = HttpProtocols.Http1AndHttp2;
            lo.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = ctx =>
                {
                    var sni = ctx.ClientHelloInfo.ServerName;
                    var isAgents = !string.IsNullOrEmpty(agentsHost) &&
                                   string.Equals(sni, agentsHost, StringComparison.OrdinalIgnoreCase);
                    return ValueTask.FromResult(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = server,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = isAgents,
                        // Any certificate is fine here (agents use self-signed ones); trust comes from the pin checked in AgentCertAuthHandler.
                        // But a missing certificate must fail the handshake on the agents name.
                        RemoteCertificateValidationCallback = (_, cert, _, _) => !isAgents || cert is not null,
                        ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                    });
                },
            });
        }));
    }

    private static X509Certificate2 LoadServerCertificate(string? thumbprint, string? pfx)
    {
        if (!string.IsNullOrEmpty(thumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: true);
            if (found.Count == 0) throw new InvalidOperationException($"Server certificate {thumbprint} not found in LocalMachine\\My (or not valid).");
            return found[0];
        }
        // PFX file: password comes from an environment variable, never from a config file.
        var pwd = Environment.GetEnvironmentVariable("ICC_TLS_PFX_PASSWORD");
        return X509CertificateLoader.LoadPkcs12FromFile(pfx!, pwd);
    }
}
