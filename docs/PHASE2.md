# Phase 2 review notes

Scope: projects, components, deployment targets, onboarding/mapping screen, project role assignments, drift and the daily reconciliation job.
Also done in this phase (promised in the Phase 1 notes): **agent certificate renewal**.

## What you can do now

| Screen | What |
|---|---|
| Projects | List (only projects you hold a role in; Platform Admin and Auditor see all). Create project (Platform Admin) with an optional Entra owner group that becomes Project Owner. |
| Project detail | **Components** with their targets grouped by environment (server, site/app, folder, pool, deploy order, status). **Members** (role × environment grid and list). **Settings** (Prod approvers 1 or 2, self-approval per environment, rename, deactivate). History is a placeholder for Phase 3. |
| Onboarding | Every site and sub-application found by discovery, with a suggestion from folder / site name / host name. **Nothing is saved from a suggestion.** Admin maps; Project Owner sends a link request; admin approves or rejects. |
| Drift | Changes made in IIS outside the portal, with before/after and acknowledge. Linked sites that were deleted or renamed become **Missing**, changed folder or pool becomes **Drifted**. |
| My access | Every role you hold and why (directly or through which group), with expiry. |
| Dashboard | Open drift and blocked targets added. |
| Server detail | Agent certificate expiry. |

## Rules that are enforced (and tested)

- **Link identity** is `ServerId + IIS site id + application path`. The folder and pool are copied from the last discovery, never typed.
- **Environment comes from the server.** A Dev server can only hold Dev targets.
- A site/application can have **one** target. A component has one target per environment per server.
- **Pool rule:** a pool used by another component blocks the link. Across projects the message says so. A Platform Admin can waive it **with a written reason**, stored in `LinkExceptions` and the audit log.
- **Folder overlap is blocked and cannot be waived** (parent/child or same folder). Otherwise Full Replace on one component could delete another component's files. See decision 1 below.
- **Roles:** Owner can grant Viewer and Deployer only. Approver and Owner are Platform Admin only (decision D4). Individual users need an expiry. A Platform Admin gets View/members/link rights on every project but **never** Submit or Approve from the platform role.
- **Self-approval** is stored per project and environment, off by default, every change audited (decision D5). It is only used in Phase 3.
- **Missing / Drifted targets** are blocked for deployment (Phase 3 reads the status). Drifted values are **not** silently adopted; an admin must accept them, and the link rules run again.
- A server with targets cannot be removed.

## What was verified, and how

| Area | Verified by |
|---|---|
| Link rules, drift diff, verifier, suggestions, project access, grant rules | 63 new Core unit tests |
| Projects, members, targets, reconciliation, link requests, drift scoping, certificate renewal | 64 new service tests on SQLite |
| Agent renewal timing, including the loop found in the manual run | 6 new agent tests |
| Whole flow in a real browser against the real portal and a real agent over mutual TLS | manual run (Linux, fake IIS data): approve server, create projects and components, map from a suggestion, pool exception with reason, members, self-approval switch, certificate renewal, IIS changed behind the portal's back -> Missing -> drift list -> accept |
| Phase 1 items on Windows | **GitHub Actions run 1 passed**: 6 IIS integration tests on real IIS (Windows Server 2025), single-file agent reading IIS, installer validation |
| Real install on Windows (**passed, run 4**) | CI job on Windows Server 2025: real installer in Windows PowerShell 5.1, local service account created and made administrator, service running as that account, config folder locked to administrators, registration over real mutual TLS (portal on Kestrel, certificate from the Windows store, two host names on one port), agent shows Pending, wrong-environment approval refused, approve, ReadOnly start, discovery of the real IIS, service restart reconnects with the stored certificate, second install refused, uninstall |

## Bugs found by the real Windows install (fixed)

- The installer created the local service account with a 49-character description; Windows allows 48. It would have failed on every non-domain server. The installer now also explains how to recover when it fails after registering.
- The CI portal was killed when its step ended (CI problem, not a product problem).

## Bugs found by the manual run (fixed)

1. The dropdowns in the mapping dialog showed raw GUIDs. 
2. A project suggestion did not pre-fill unless the component also matched.
3. The "reason" box did not enable the button until the field lost focus.
4. **Certificate renewal looped** when the renewal window was longer than the certificate's life (only reachable through a developer setting, but it piled up certificates). A new certificate is now never renewed again within 24 hours.

## Decisions I made that you should check

1. **Overlapping folders are blocked, not warned.** The common setup "site root as one component, `/api` sub-application as another" is therefore **not allowed** when the sub-application's folder is inside the root's folder. If you have many of these, Phase 3 can exclude nested target folders automatically. Tell me before I build deployments.
2. **Project Owners can see the onboarding list** (all unassigned IIS items on all servers: names, folders, pools). Without this they cannot ask for a link. If that exposure is not acceptable, the alternative is a free-text request that an admin completes.
3. **Roles per environment have no "all environments" option** (deny by default). Owner is project-wide. The UI adds one row per environment.
4. **Platform Admin cannot approve or deploy through the platform role**, only through an assigned project role. (Matches the brief: "a user can act on a component only with the right role for the owning project".)
5. **Protected paths** are seeded at component creation (`web.config`, `appsettings*.json` as config; `uploads\`, `generated-files\` as data) and shown read-only. Editing, the "confirmed" gate for Full Replace and the diff come in Phase 3.
6. **Reconciliation** runs after every discovery (6-hourly, on approval, on request) and daily at 02:30 UTC re-checks every link against the latest snapshot, even for offline servers.
7. **Not done on purpose:** the quarterly access-review CSV (Phase 5 "reports"), Microsoft Graph group lookup (needs your tenant admin), editing protected paths.

## Still unverified

- **gMSA installs** (domain servers): the CI machine is not domain joined, so only the local-account path ran for real.
- **The installer under an AV/EDR product** and on servers with hardened policies (for example 'log on as a service' managed by Group Policy, which can overwrite what the script sets).
- SQL Server (migrations were generated, never applied; tests use SQLite, where text matching is case-sensitive).
- Entra sign-in, Hangfire, Kestrel loading the server certificate from the Windows store (the new CI job covers the last one on a Windows runner).
- Anything Phase 3 or 4: deployments and IIS changes do not exist yet.
