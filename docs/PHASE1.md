# Phase 1 review notes

Scope: solution structure, login and roles, server registration, agent connection, read-only discovery, discovery report.

## What was verified, and how

| Area | Verified by | Where it ran |
|---|---|---|
| Rules (access, tokens, audit chain, report) | 27 unit tests | Linux |
| Server service, registration, approval, revoke, discovery storage, scoping, audit, roles, CSV | 65 unit tests on SQLite | Linux |
| Agent path guard, command checks, local config, "no secrets in discovery" guard | 87 unit tests | Linux |
| Every page has `[Authorize]` or `[AllowAnonymous]` | unit test | Linux |
| **Real TLS path**: UI name vs agents name on one port, client certificate required only on the agents name | manual run | Linux |
| **Real agent**: register with token, proof of possession, connect over mutual TLS, Pending, approve in a browser, automatic discovery, report, audit chain check, revoke, revoked agent refused | manual run with Chromium | Linux (fake IIS data) |
| Release build with warnings as errors; single-file `win-x64` publish | build | Linux |
| PowerShell installer: syntax only | PowerShell parser | Linux |

## NOT verified (needs Windows). Treat as untested until the Windows CI job is green

- `IisInspector` against real IIS (the 6 integration tests are written; they skip on Linux).
- The single-file agent actually starting on Windows and reading IIS (CI step "Single-file agent can read this machine's IIS").
- `Install-Agent.ps1` doing a real install: service creation, gMSA, local account, secedit rights. CI only runs `-ValidateOnly`.
- Machine-store certificate creation (`LocalMachine\My`, non-exportable key) on Windows.
- SQL Server: migrations were generated but never applied here (tests use SQLite). SQLite compares text case-sensitively, SQL Server does not.
- Hangfire (needs SQL Server) and Entra sign-in (needs your tenant).
- Kestrel loading the server certificate from the Windows store by thumbprint.

## Bugs found by the manual run (and fixed)

1. The TLS check accepted a client with no certificate on the agents name.
2. The agent hub was marked anonymous, which silently removed its certificate requirement.
3. Static files and `/_blazor` were redirected to sign-in, so the sign-in page could not load.
4. A page without `[Authorize]` (Home) became public once (3) was fixed. A unit test now guards this.

## Differences from the design document

- **Live updates** use an in-process event bus, not a separate `/hubs/ui` hub. Blazor Server already has its own live connection. A second portal node would need a backplane (design risk 15).
- **Settings page** is read-only. Editable settings, naming conventions, retention and protected-path defaults come in later phases.
- **Hangfire** runs only the 6-hourly discovery job. It is off in Development.
- **Registration** additionally requires the certificate used for the TLS connection to be the one being registered (proof of possession). Not in the design; added because it is cheap and closes a gap.
- **Agent `discover` command** added so the data the agent collects can be inspected by hand.
- **Dev sign-in** page added for local work only.

## Known gaps and decisions for you

1. **Agent certificate expires after 2 years and Phase 1 has no renewal.** The agent will silently stop connecting. Renewal should be done in Phase 2 (earliest failure: October 2028). Until then, re-register with a new token.
2. **Entra group overage:** if a user belongs to more than 200 groups, Entra omits the `groups` claim and group-based roles will not apply. A Microsoft Graph lookup is needed (design 0.14, needs a Graph permission from your tenant admin).
3. **Agent logs go to the Windows Application event log only.** No file log or log shipping yet.
4. **Single portal node.** Agent connection tracking and the event bus are in memory.
5. **Bootstrap admins** in config bypass the database. Remove them after the first Platform Admin group is assigned.
6. **Pool sharing report is strict:** a site root and its own sub-application on the same pool count as "shared". This follows the brief literally and will be noisy on default setups. Say if you want sub-applications of the same site excluded.
7. **Discovery only runs on request, on approval, and every 6 hours.** Drift detection is Phase 2.

## Try it

See the README. For a full local walkthrough without Windows, run the portal in Development, then register an agent
built from this repo; on non-Windows machines the agent returns fake sample IIS data (it refuses to do so on Windows).
