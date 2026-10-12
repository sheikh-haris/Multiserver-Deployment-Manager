# IIS Control Center

Internal portal to manage IIS across many Windows servers and (from Phase 3) deploy code to them with approval.
Design: [docs/DESIGN.md](docs/DESIGN.md). Review notes: [Phase 1](docs/PHASE1.md), [Phase 2](docs/PHASE2.md).

```
src/IisControlCenter.Contracts   messages shared by portal and agent
src/IisControlCenter.Core        rules and domain model (no database, no IIS)
src/IisControlCenter.Data        EF Core model and SQL Server migrations
src/IisControlCenter.Web         Blazor + MudBlazor portal, agent hub, Hangfire
src/IisControlCenter.Agent       Windows service that runs on each IIS server
src/IisControlCenter.Agent.Iis   read-only IIS inspection (Microsoft.Web.Administration)
installer/Install-Agent.ps1      installs the agent on a server
tests/                           unit tests, plus Windows-only IIS integration tests
db/permissions.sql               make the audit log insert-only for the portal's database login
```

## Run the portal locally (Linux, macOS or Windows, no SQL Server needed)

```
dotnet run --project src/IisControlCenter.Web --launch-profile https
```
Development mode uses SQLite, a "pick a person" sign-in page, and no Hangfire. It exists only when
`ASPNETCORE_ENVIRONMENT=Development`; the portal refuses to start in any other environment with `Auth:Mode=Dev`.

## Production settings (`appsettings.Production.json` or environment variables)

| Setting | Meaning |
|---|---|
| `ConnectionStrings:Default` | SQL Server. Use integrated security. The login must not be db_owner (see `db/permissions.sql`) |
| `Auth:Mode` | `Entra` |
| `AzureAd:TenantId`, `ClientId` | Entra app registration (redirect URI `https://<portal>/signin-oidc`, add the **groups** claim) |
| `AzureAd:ClientSecret` | **Do not put in a file.** Supply from Key Vault or an environment variable |
| `Auth:BootstrapAdmins` | Object ids or user names that are Platform Admin before any role exists. Remove once real roles are set |
| `Portal:AgentsHost` | DNS name agents use, e.g. `agents.portal.example.local` (decision D2) |
| `Portal:Tls:Thumbprint` | Server certificate in `LocalMachine\My`. It must cover both the portal name and the agents name |
| `Portal:HttpsPort` | Default 443 |

Apply the database once: `dotnet ef database update -p src/IisControlCenter.Data` (using a migration account), then run `db/permissions.sql`.

## Try the whole flow without Windows

Development mode only. Run the portal (above), then in `src/IisControlCenter.Agent` use `dotnet run -- register ...` and `dotnet run`.
On non-Windows machines the agent returns fake IIS data; set `ICC_SAMPLE_VARIANT=2` to imitate someone changing IIS Manager by hand and watch **Drift**.

## Install an agent

1. Portal: **Servers → Add server**. Copy the one-time token (valid 1 hour).
2. On the IIS server, as Administrator: run `installer\Install-Agent.ps1` with the printed command.
   Add `-ValidateOnly` first to check everything without changing anything.
3. Portal: open the server, **Approve** it (the environment must match what you typed on the server).
4. Discovery runs automatically. See **Discovery report**.

Build the agent: `dotnet publish src/IisControlCenter.Agent -c Release -r win-x64 -p:PublishSingleFile=true`.
`IisControlCenter.Agent.exe discover` prints exactly what the agent would send. Show it to your security team.

## Tests

```
dotnet test --filter "FullyQualifiedName!~IntegrationTests"      # unit tests, any OS
dotnet test tests/IisControlCenter.Agent.IntegrationTests        # needs Windows + IIS + Administrator; skipped elsewhere
```
CI (`.github/workflows/ci.yml`) runs three jobs: Linux unit tests; a Windows job with IIS (integration tests, single-file agent, installer checks);
and a Windows end-to-end job that runs the real installer, starts a portal on the runner, and checks registration, approval, discovery, restart and uninstall.
