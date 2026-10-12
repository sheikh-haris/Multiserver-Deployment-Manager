# Phase 3 review notes: Deployments

Scope: chunked package upload and validation, diff, approval queue, execution on the agent with backup, health check,
automatic rollback and crash recovery, live log, manual rollback, schedule, notifications, protected-path editor, reports.

## What you can do now

| Screen | What |
|---|---|
| **Deployments** | Tabs: waiting for my approval, my requests, open, history. Badges: DIRECT TO PROD, SELF-APPROVED, ROLLBACK. |
| **New deployment** (wizard) | 1 pick component + environment. 2 upload a .zip (sent in parts, resumes after a dropped connection) or reuse a stored package. 3 servers, Overlay or Full Replace, source reference, notes, optional run time. 4 **review**: the portal asks each agent what is on the server *now* and shows added / replaced / deleted / unchanged / protected files per server. |
| **Request page** | Facts, approvals, per-server file list, masked before/after for forced config files, execution table, **live log**, Approve / Reject (reason needed) / Cancel / change run time / **Roll back**. A "partly deployed" banner when servers ended on different versions. |
| **Project page** | "Deploy to <env>" buttons, protected-path editor (add/remove, data-folder confirmation), notification email, history tab. |
| **Deployment reports** | Direct-to-Production deployments per project and month, and every self-approved request. |
| Dashboard | Waiting approvals and recent deployments (also for people who only hold project roles). |

## Rules that are enforced (and tested)

- **Who**: submit needs Deployer for that project *and* environment; approve needs Approver. Platform Admin alone can do neither (decision from Phase 2).
- **Requester cannot approve own request**, unless the project allows it for that environment. Then it is marked SELF-APPROVED, audited as its own action and listed in the report (D5).
- **Direct to Prod** = the exact package (by SHA-256) has not succeeded on Staging. Needs a reason, a badge, and the project's 1 or 2 approvers. Blocked when the component says "Staging first". A package that did succeed on Staging is a Promote: 1 approver.
- **Protected files**: config files are copied only if absent; data folders are never written, deleted or backed up. The agent merges the portal's list with its own local list, so the portal cannot shrink protection. Forcing a config overwrite needs a reason, shows a masked diff (passwords, keys, connection strings hidden; same value = same code), and is audited.
- **Full Replace** is blocked until a Platform Admin confirms the component's data folders. Any change to the list voids that. More than 20 deletions or more than 10 % of the folder shows a red warning.
- **Package safety**: only .zip, size limit, entry-count and expansion-ratio limits, no path tricks (`..`, absolute, drive letters, symlinks, duplicates), no .exe/.bat/.cmd/.ps1/.msi unless the component allows the type. Antivirus: portal Defender if it can run, otherwise **each target server scans before extracting**; if neither can scan, the deployment is refused (fails closed).
- **What was approved is what runs**: the portal stores what the folder looked like at submit. The agent compares again before touching anything. If a package path changed or Full Replace would delete a file the approver did not see, nothing is deployed and the request goes to **NeedsReapproval**.
- **Execution**: one server at a time in the set order, stop at the first failure. Steps: validate, download (resumes), scan, extract, snapshot check, backup, `app_offline.htm`, copy, recycle the target's own pool, online, health check. Health check or copy fails -> automatic restore from backup. A failed restore is shown as ROLLBACK FAILED and needs a person.
- **Re-check at execution time**: the roles that allowed the request must still be active when it starts. Otherwise NeedsReapproval.
- **Crash recovery**: if the agent or server dies mid-run, the agent undoes the run at next start and reports. Results are kept on disk until the portal confirms them.
- **Manual rollback**: only the latest successful run of a target; outside Production no approval, in Production one approver. The agent only accepts backup ids it made itself.
- **Expiry**: 7 days from submit. Package retention 90 days (kept while an open request needs it).
- **Uploads**: custom header on every call (no CORS, so other websites cannot post for a signed-in user). Large payloads (package, plan, manifest) go over HTTPS keyed by run id and agent certificate, not over SignalR.

## Verified by

| Area | How |
|---|---|
| Shared rules (planner, masker, zip validator, diff, state machine, approval and submit rules) | 248 Core tests |
| Executor: backup, restore, health, rollback, crash recovery, protected files, path safety | 165 agent tests |
| Portal: upload (resume, damaged chunk, other user, dangerous zip, AV modes), submit/approve/reject/cancel/expire, promote vs direct, self-approval, orchestrator (order, stop on failure, offline agent, timeout, schedule, role removal, wrong-server results, repeats), rollback, retention, notifications | 188 web tests (fake agent) |
| Whole flow in a real browser, real portal, real agent over mutual TLS (Linux, sample IIS data): `tests/Smoke/run.sh` | upload refusal of a zip-slip package, review, submit, requester has no Approve button, approve, live run, protected files kept, **bad release fails its health check -> automatic restore**, manual rollback |
| Real Windows: installed service + real IIS + portal | CI job `windows-e2e` (see result at the bottom) |

## Decisions I made that you should check

1. **Deployments are allowed on ReadOnly servers.** ReadOnly/Managed governs IIS *configuration* changes (Phase 4). The agent still only writes inside its local roots. Say if you want deployments to need Managed.
2. **Pool recycle only for the target's own pool**, never other pools.
3. **No Hangfire for execution.** A 5-second timer reads the database, so a portal restart loses nothing and it works with Hangfire off. Hangfire stays for discovery/reconciliation.
4. **Role removal is seen at execution only for role *assignments*.** If someone leaves an Entra group, the portal learns this when they next sign in, not at execution time. Limit of not calling Graph.
5. **One portal node** is assumed (in-memory waits, locks). Two nodes would need shared state.
6. **Times on screen use the portal server's time zone**, not the browser's (Blazor Server). Schedule input is in that zone too.
7. **A "NeedsReapproval" request is not revived**; the requester submits a new one (fresh diff).
8. Phase 2 questions still unanswered, defaults kept: overlapping folders blocked; Owners can see the onboarding list.

## Risks to know about

- The antivirus path depends on Defender being usable. If the portal cannot scan and a target's Defender is off or blocked, deployments stop. This is deliberate.
- Backups live on each IIS server (zip, excluding data folders, last 5). The agent refuses to start a backup without free space of about 2x the folder. Disk monitoring is yours.
- Package store and backups are only as safe as the folder permissions on those machines. Back up the package folder if you need old packages after 90 days.
- Email needs an SMTP relay (`Notifications:Smtp`). Without it the outbox keeps retrying 5 times then marks failed. Nothing blocks.

## New settings

`Deployments:PackageStorePath`, `MaxPackageMb`, `RetentionDays`, `BackupsToKeep`, `HealthTimeoutSeconds`, `RunTimeoutMinutes`, `AgentOfflineGraceMinutes`, `AntivirusMode` (Auto | Portal | Agent), `PublicBaseUrl`; `Notifications:Smtp:*`. Contract version is now 2: **agents must be upgraded** (the portal refuses older ones).
No new SQL permissions beyond the Phase 1 script (tables are created by the migration `Phase3Deployments`).
