# IIS Control Center — Design Document (v0.1, for approval)

Status: **Draft. No code written. Waiting for your approval and answers in section 12.**

---

## 0. Read this first: conflicts and gaps in the requirements

These are places where the brief contradicts itself, or where the stated threat model ("assume the central app is compromised") is not met by the stated design. Each has a proposed fix. I need your decision on the ones marked **[DECIDE]**.

1. **Environment, mode and allowed paths cannot come from the portal.** The brief says the agent "refuses any command for an environment it does not belong to" and "refuses writes outside the path the portal passes". If the portal is compromised, it simply sends a different environment or path.
   - Fix: the installer writes a **local, admin-protected config file** with: environment, allowed root paths, and (optionally) "Managed mode allowed". The agent only accepts commands that fall **inside** this local config. The portal's values are only a request, checked against local limits.
   - Side effect: the "admin assigns environment on approval" step becomes "admin **confirms** the environment the installer declared". If they differ, registration is rejected. **[DECIDE: accept this]**

2. **A compromised portal can still deploy bad code.** The allow-list stops arbitrary commands. It does not stop "deploy a malicious DLL to every server", because deploying DLLs is the product.
   - Reduce, not remove: the approver's decision is signed (see 7.4) and the agent verifies the signature against an approver key that the portal cannot use alone. This is optional and costs effort. **[DECIDE: Phase 3 or later?]**
   - Be honest in the risk register: blocking `.exe/.bat/.ps1` is hygiene, not security. `.dll` and `.aspx` are the real payload.

3. **Discovery says "never send config file contents", but force-overwrite needs a before/after config diff.** These conflict.
   - Fix: a narrow exception. Only for files the requester explicitly marked as force-overwrite, the **agent** reads the live file, **masks secrets on the agent**, and sends only the masked text. Never for discovery. Logged in the audit trail. Masking is pattern-based, so it can miss secrets; the approver UI says so. **[DECIDE: accept, or drop the content diff and show only hash changed/unchanged]**

4. **Diff needs the agent.** The portal cannot see the live folder. The diff is built from a file manifest (path, size, hash) sent by the agent at submit time. So the target agent must be online to submit a request. Stated here so it is not a surprise.

5. **"Snapshot compare at execution" will give false alarms.** Live folders contain logs and temp files that change all the time. Comparing the whole folder would flag almost every request as Needs Re-approval.
   - Fix: compare only the paths the diff touches (files to add, change, delete). Ignore protected data folders.

6. **Full Replace is the biggest data-loss risk.** Anything not in the zip is deleted. If a component's data folder was never listed as protected, the data is gone.
   - Fix: (a) Full Replace is blocked for a component until its protected data list has been explicitly confirmed (even if the answer is "none"). (b) Diff shows a red warning when deletions exceed a threshold (default: more than 20 files or 10 % of files). (c) The agent refuses to delete anything outside the target folder or inside a protected data folder, regardless of the portal. (d) Backup runs before any delete.

7. **mTLS on 443 shared with the browser UI is awkward.** If the same hostname asks for client certificates, browsers may show a certificate picker to users, and a reverse proxy that ends TLS breaks mTLS completely.
   - Fix: agents use a **separate hostname** (e.g. `agents.portal.local`, same port 443, second IP or SNI-based binding) where client certs are required. UI hostname never asks for one. Portal must run on Kestrel (or a proxy that passes the client cert through). **[DECIDE: separate hostname, or separate port 8443 if firewall rules allow]**

8. **"Agent creates its own client certificate" needs a trust anchor.** A self-made cert proves nothing by itself.
   - Fix: at registration the agent creates a non-exportable key in the machine store and a self-signed cert, sends the public cert plus the one-time token. The portal **pins the thumbprint** after admin approval. Revocation = mark thumbprint revoked, drop the SignalR connection, reject on next handshake. Renewal = agent presents the old cert and gets a new one pinned (every 12 months). No CA needed. (Alternative: enterprise CA. More moving parts.)

9. **Per-environment service accounts only help across servers.** One server belongs to one environment, so it has one agent identity anyway. The real value: the Dev gMSA is allowed on Dev servers only, and a leaked Dev credential cannot log into a Prod server. I will document it that way and not oversell it.

10. **Shared app pools vs. "block sharing across projects".** Existing servers almost certainly share pools between unrelated sites. A hard rule would block onboarding of those sites.
    - Fix: the rule is enforced for **new links**. Existing shared pools are listed in the discovery report and need a Platform Admin **exception** (recorded, with reason) before linking. **[DECIDE]**
    - Also, "one pool per component" is stricter than "one pool per project". I assume: a pool can be linked to **one component** only. **[DECIDE]**

11. **HTTPS certificate management.** "Manage certificates" could mean uploading PFX files through the portal. That puts private keys in the central app, which contradicts "secrets never in DB" and the compromise model.
    - Fix: the portal only **binds certificates already installed** on the server (chosen by thumbprint). Getting the cert onto the server stays outside the tool for now. **[DECIDE]**

12. **Rollback after a multi-server failure.** "Stop on first failure" leaves server 1 on new code and server 2 rolled back. That is a mixed state, and a rollback of server 1 on Production needs a new approval.
    - Fix: request ends `Failed` or `RolledBack` for the failing target; earlier targets stay `Succeeded` and the request shows **"Partial: 1 of 2 servers updated"** with a one-click "Roll back all" (needs approval on Production). I add this as a UI/state rule, not a new request state.

13. **Scheduled and delayed execution vs. offline agents.** If the agent is offline at the scheduled time, the run waits up to a grace period (default 60 min), then fails with "agent offline". It never runs late silently.

14. **Permission check at execution time** needs the portal to re-read group membership with no user logged in. Needs a Graph/AD app permission to read group members, and a short cache. Without it, group-based rights cannot be re-checked at execution.

15. **High availability.** One portal node is a single point of failure. A second node needs a SignalR backplane and Hangfire already supports SQL. I design for one node first, without blocking two.

16. **Linux sandbox note.** The environment I am working in has **no .NET SDK and no Windows/IIS**. I can write code but cannot compile or run the agent or IIS tests here. You will need a Windows CI runner or a test IIS server to run phase tests. I will not claim "tests pass" for anything I could not run.

17. **Defender scanning.** Calling `MpCmdRun.exe` from the portal means the portal host must be Windows with Defender enabled. Defender scans inside archives only partly; large or nested zips may be skipped. It is a useful layer, not proof of safety. Defender may also quarantine the file mid-scan; the upload handler must treat "file vanished" as `Infected`.

---

## 1. Components

```
 Browser ──HTTPS (UI host)──►  Portal (ASP.NET Core, Kestrel)
                               ├─ Blazor Web App (Interactive Server) + MudBlazor
                               ├─ Web API  (/api/ui, /api/upload, /api/agent)
                               ├─ SignalR  (/hubs/ui  and  /hubs/agent)
                               ├─ Hangfire server (jobs)
                               ├─ EF Core ──► SQL Server
                               ├─ Package store (file share / Blob)
                               └─ Defender scan (MpCmdRun)

 IIS server ◄── Agent (Windows Service) ──outbound mTLS 443──► Portal agent host
                ├─ SignalR client (commands in, events/logs out)
                ├─ HTTPS download of packages (Range, resumable)
                ├─ Microsoft.Web.Administration (IIS)
                └─ Local config (env, roots, mode limit)  ← source of truth for safety
```

**Solution layout**

```
src/
  IisControlCenter.Web          Blazor + API + hubs + Hangfire host
  IisControlCenter.Core         Domain model, rules, state machine (no I/O)
  IisControlCenter.Data         EF Core, migrations
  IisControlCenter.Contracts    Shared message DTOs (portal + agent)
  IisControlCenter.Agent        Worker Service
  IisControlCenter.Agent.Iis    IIS + file operations (testable, Windows only)
tests/
  *.Tests                       Unit tests (Core, Web services)
  *.Agent.IntegrationTests      Needs Windows + IIS (CI runner or test server)
installer/
  Install-Agent.ps1
docs/
```

Rules: `Core` has no references to EF, SignalR or IIS. `Contracts` is the only project shared with the agent. The agent never references `Data` or `Web`.

---

## 2. Data model

Conventions: `Id` = GUID (sequential), all tables have `CreatedAt`, `CreatedBy`. `Environment` = FK to `Environments` (Dev, Staging, Prod; has `IsProduction`, `SortOrder`) so a fourth environment later needs no code change.

### 2.1 Servers and agents
| Table | Columns |
|---|---|
| Servers | Id, Name, Hostname, Environment, Mode (ReadOnly/Managed), AgentStatus (Pending/Online/Offline/Revoked), IsDomainJoined, AgentVersion, OsVersion, LastSeenAt, LastDiscoveryAt, DeclaredEnvironment, ApprovedBy, ApprovedAt |
| RegistrationTokens | Id, ServerId, TokenHash, ExpiresAt (1 h), UsedAt, CreatedBy |
| AgentCertificates | Id, ServerId, Thumbprint, NotAfter, Status (Active/Revoked/Superseded), RevokedBy, RevokedAt, RevokeReason |

### 2.2 Discovered IIS state (read-only snapshot, replaced on each discovery)
| Table | Columns |
|---|---|
| IisSites | Id, ServerId, IisSiteId, Name, State, PhysicalPath, AppPoolName, MappingStatus (Unassigned/Mapped/Missing), DiscoveredAt |
| IisBindings | Id, IisSiteRowId, Protocol, Host, Port, IpAddress, CertThumbprint |
| IisApplications | Id, IisSiteRowId, Path, PhysicalPath, AppPoolName |
| IisVirtualDirs | Id, IisApplicationRowId, Path, PhysicalPath |
| IisAppPools | Id, ServerId, Name, IdentityType, RuntimeVersion, PipelineMode, State (**no password, ever**) |
| IisCertificates | Id, ServerId, Thumbprint, Subject, NotAfter, Store |
| ServerDotNetVersions | Id, ServerId, Kind (Runtime/Hosting bundle), Version |
| DriftEvents | Id, ServerId, ObjectType, ObjectRef, Kind (Changed/Created/Deleted/Renamed), Before, After, DetectedAt, AcknowledgedBy |

### 2.3 Projects and targets
| Table | Columns |
|---|---|
| Projects | Id, Name, Slug, Description, OwnerGroup, IsActive, ProdApproversRequired (1 or 2) |
| Components | Id, ProjectId, Name, Slug, Type (Web/Api/MobileApi/...), HealthCheckPath, ExpectedHealthStatus, AllowedFileTypes (extra allowed extensions), RequiresStagingBeforeProd (bool, default false) |
| DeploymentTargets | Id, ComponentId, Environment, ServerId, IisSiteId, AppPath, PhysicalPath, AppPoolName, HealthCheckUrl, DeployOrder, Status (Active/Missing/Disabled), LastVerifiedAt, LinkExceptionId (nullable) |
| ProtectedPaths | Id, ComponentId, Path, Kind (ConfigFile/DataFolder), IsDefault |
| ProtectedPathsConfirmed | ComponentId, ConfirmedBy, ConfirmedAt (gate for Full Replace) |
| LinkExceptions | Id, Reason, GrantedBy, GrantedAt (for shared pools) |

Uniqueness (enforced in SQL, not only in code):
- `(ServerId, IisSiteId, AppPath)` unique among active targets.
- Pool rule: unique index on `(ServerId, AppPoolName)` among active targets unless `LinkExceptionId` is set.
- Cross-project check lives in the link service and is tested; SQL index covers the same-server case.

### 2.4 Access
| Table | Columns |
|---|---|
| RoleAssignments | Id, PrincipalType (Group/User), PrincipalId, Scope (Platform/Project), ProjectId (null for Platform), **ComponentId (null; reserved for per-component rights later)**, Environment (null = all, only for platform roles), Role, GrantedBy, GrantedAt, ExpiresAt (**required when PrincipalType = User**), RevokedAt, RevokedBy |
| AccessReviews | Id, ProjectId, Quarter, GeneratedBy, GeneratedAt |

Roles: PlatformAdmin, ServerOperator, Auditor, ProjectViewer, Deployer, Approver, ProjectOwner.

### 2.5 Deployments
| Table | Columns |
|---|---|
| Packages | Id, ComponentId, FileName, Size, Sha256, StoragePath, AvResult (Clean/Infected/Error/Pending), AvScannedAt, EntryCount, UncompressedSize, UploadedBy, UploadedAt, ExpiresAt, DeletedAt |
| UploadSessions | Id, UserId, ComponentId, FileName, TotalSize, ChunkSize, ReceivedChunks, Status, ExpiresAt |
| DeploymentRequests | Id, **Type (Deploy/Rollback)**, ComponentId, Environment, PackageId, Mode (Overlay/FullReplace), SourceReference, Notes, IsDirectToProd, DirectToProdReason, SingleServerReason, PromotedFromRequestId, RollbackOfRunId, Status, RequestedBy, RequestedAt, ScheduledAt, ExpiresAt, ApprovalsRequired, **ReleaseBundleId (null, future)**, **BundleOrder (null, future)** |
| RequestTargets | Id, RequestId, DeploymentTargetId, DeployOrder, ManifestSnapshot (JSON: file list + hashes at submit time) |
| ForcedFiles | Id, RequestId, Path, Reason, BeforeMasked, AfterMasked |
| Approvals | Id, RequestId, ApproverId, Decision (Approved/Rejected), Comment, DecidedAt, Signature (optional, see 7.4) |
| DeploymentRuns | Id, RequestId, DeploymentTargetId, Sequence, StartedAt, EndedAt, Result, BackupPath, BackupSize, FailureStep |
| DeploymentLogLines | Id, RunId, Seq, At, Level, Step, Message (kept off the Runs row so long logs do not bloat it) |
| Notifications | Id, RequestId, Kind, ToAddress, Status, Attempts, SentAt (outbox) |

### 2.6 Other
| Table | Columns |
|---|---|
| AuditLog | Id, Timestamp, UserId, UserName, Action, TargetType, TargetId, ServerId, ProjectId, Environment, Before, After, Result, Detail, CorrelationId, ClientIp, PrevHash, Hash |
| Settings | Key, Value, UpdatedBy, UpdatedAt (naming convention, max size, retention, backup count, thresholds) |
| NamingConventions | Id, SitePattern, PathPattern, IsActive |
| SiteTemplates | Id, Name, Json, CreatedBy (Phase 5) |
| ReleaseBundles / ReleaseBundleItems | Created empty now? **No.** Only the two nullable columns above. Tables come with the feature. |

**Audit log protection:** the app's SQL login gets `INSERT, SELECT` only on `AuditLog` (no UPDATE/DELETE). Each row stores a hash of the previous row, so deletion or editing by a DBA is detectable. The UI has no edit or delete.

---

## 3. API endpoints (summary)

Auth: UI and `/api/ui` use cookie login (Entra OIDC). `/api/agent` uses mutual TLS only. All state-changing UI calls require antiforgery.

| Area | Endpoint | Notes |
|---|---|---|
| Upload | `POST /api/upload/sessions` | Start. Checks role, size limit, component |
| | `PUT /api/upload/sessions/{id}/chunks/{n}` | Fixed-size chunk (default 8 MB), per-chunk SHA-256 header, resumable |
| | `POST /api/upload/sessions/{id}/complete` | Assemble, verify total hash, then validate + scan (background) |
| | `GET /api/upload/sessions/{id}` | Status for progress bar and resume |
| Packages | `GET /api/packages/{id}/download` | Platform Admin only. Audited |
| Agent bootstrap | `POST /api/agent/register` | Token + public cert. Anonymous but token-gated, rate limited |
| Agent files | `GET /api/agent/packages/{hash}` | mTLS, Range supported, only for a package tied to an active run on that server |
| Agent hub | `/hubs/agent` | Commands in, events out (see section 6) |
| UI hub | `/hubs/ui` | Live progress, logs, queue changes, agent status |
| Reports | `GET /api/reports/access-review/{projectId}.csv`, `/direct-to-prod.csv`, `/audit.csv` | Role-checked |

All other UI actions (servers, projects, approvals, IIS changes) go through application services called directly from Blazor components, not through HTTP. They are plain C# services with the same permission checks, so there is one rule set.

---

## 4. Server onboarding and discovery flow

1. Admin: **Add Server** → portal creates a `Servers` row (Pending) and a token (shown once, hash stored, 1 h expiry).
2. On the server: `Install-Agent.ps1 -PortalUrl … -Token … -Environment Prod -Roots D:\Websites -ServiceAccount …`.
   - Domain: `-Gmsa 'CORP\svc-icc-prod$'`. Non-domain: script creates a local account with a random password stored in DPAPI; password never leaves the server.
   - Script installs the single-file binary, writes the local config, sets file ACLs (agent config writable by Administrators only), registers the service.
3. Agent generates keypair + cert, calls `register`, then connects over mTLS.
4. Server shows **Pending**. Admin sees declared environment, hostname, OS. Admin approves (confirm environment).
5. Server starts in **ReadOnly**. Agent runs discovery on connect and every 6 hours. Admin can trigger it manually.
6. Discovery results fill the discovered tables. Items are **Unassigned**. The onboarding screen groups suggestions by host header and path prefix; only a human click creates a mapping.
7. Reconciliation (daily Hangfire job) asks each online agent for a fresh discovery, compares with mapped targets, writes `DriftEvents`, marks targets **Missing** if the site ID or path is gone.

**Discovery report** (query over the snapshot): unassigned sites; pools used by more than one application; pools on LocalSystem; certificates expiring in ≤ 30 days.

**What discovery never collects:** pool passwords, connection strings, any file content, certificate private keys.

---

## 5. Deployment state machine

States: `Draft, Submitted, Approved, Scheduled, Running, Succeeded, Failed, RolledBack, Rejected, Expired, Cancelled, NeedsReapproval`.

```
Draft ─submit─► Submitted ─approve (all required)─► Approved ─(has schedule)─► Scheduled ─time─► Running
   │                │  │                               │                                        │
   │                │  └─reject─► Rejected             └─start now─────────────────────────────►│
   │                └─7 days──► Expired                                                         │
   └─cancel─► Cancelled   (cancel allowed from Draft/Submitted/Approved/Scheduled)               ▼
                                                    ┌── all targets OK ──────────► Succeeded
                                                    ├── failure, restore OK ─────► RolledBack
                                                    ├── failure, restore failed ─► Failed   (alarm)
                                                    └── live folder changed ─────► NeedsReapproval ─► Submitted (new diff)
```

Rules:
- Only `Core` changes status, through one `RequestStateMachine` class. Every transition writes an audit row. Illegal transitions throw.
- **Two approvers (Direct-to-Prod):** the request stays `Submitted` until `ApprovalsRequired` distinct approvers (neither the requester nor each other) have approved. Any single rejection ends it.
- **Permission checks** run at submit, at each approval, and again when a run starts (`Approved/Scheduled → Running`). If the requester or approver lost their role since, the request moves to `NeedsReapproval`.
- **Rollback requests** use the same machine (`Type = Rollback`). On non-Prod environments they auto-approve for a Deployer; on Prod they need an Approver.
- Per-target results live in `DeploymentRuns`; request status is derived from them (see 0.12).
- Backups beyond N are deleted by the agent after a successful run, never before.

---

## 6. Agent message contract

Transport: SignalR over the agent host. JSON, versioned (`ContractVersion` in handshake; portal refuses agents older than a minimum).

Every command carries: `CommandId` (GUID, idempotency key), `IssuedAt`, `ExpiresAt` (short), `ServerId`, `Environment`, `CorrelationId`. The agent remembers recent `CommandId`s and replays the stored result instead of repeating work.

**Portal → Agent (allow-list, nothing else exists in the code)**

| Command | Purpose | Safety on agent |
|---|---|---|
| `RunDiscovery` | Collect IIS inventory | Read-only |
| `GetFolderManifest` | Hash list of a target folder (for diff/snapshot) | Path inside allowed roots; excludes protected data folders |
| `GetMaskedFile` | Masked content of one forced config file | Only if file is a protected config path; masked on agent |
| `Deploy` | Run the full deployment pipeline | See 7.3 |
| `Rollback` | Restore a named backup | Backup must be one this agent created |
| `HealthCheck` | Call a URL on the same server | Host must resolve to loopback or a local binding |
| `SetPoolState / SetSiteState` | start/stop/recycle | Managed mode only; object must be a mapped target (Phase 4 adds free IIS ops) |
| `Phase 4 only:` `CreateSite, UpdateSite, DeleteSite, CreatePool, UpdatePool, DeletePool, CreateApp, CreateVDir, UpdateBindings, CreateFolder, GrantFolderAcl` | IIS management | Managed mode + local `AllowManaged`; roots enforced; idempotent; before/after returned |
| `Ping`, `Disconnect`, `CancelCommand` | Housekeeping | — |

**Agent → Portal**

| Message | Purpose |
|---|---|
| `Hello` (version, host, OS, declared env, mode, capabilities) | On connect |
| `Heartbeat` (every 30 s; 90 s without = Offline) | Health |
| `DiscoveryResult` | Inventory |
| `CommandAccepted / CommandRejected(reason)` | Ack. Rejections are audited |
| `StepProgress(runId, step, status)` | Live pipeline progress |
| `LogLines(runId, lines[])` | Batched live log, sensitive values masked |
| `RunCompleted(runId, result, backupPath, failureStep)` | Final result |
| `ChangeApplied(commandId, before, after)` | IIS change records for audit |

**Resilience:** commands are stored in a DB outbox until acked. If the agent is offline, they wait (up to the request's grace period). If the connection drops mid-run, the agent finishes (or rolls back) locally and sends the result on reconnect. A run is never "half done and forgotten": on service start the agent checks for an unfinished run marker and completes the rollback.

---

## 7. Security model

### 7.1 Trust boundaries
- Portal: runs under its own account with no admin rights on any IIS server, no WinRM, no PowerShell remoting.
- Agent: the **only** component with admin rights on its server. It trusts its local config first, the portal second.
- DB: portal login has no `sysadmin`; `AuditLog` is insert/select only.

### 7.2 Authentication and authorization
- Users: Entra ID OIDC (or AD via the same abstraction). Roles from group claims. If a user is in too many groups for the token (> 200), the portal falls back to a Graph lookup.
- One `IAccessEvaluator` in `Core`: `Can(user, action, resource)`. Deny by default. Used by UI, services and the execution-time check. No permission logic in components.
- Platform rights and project rights are checked by separate methods and can never satisfy each other.
- Grant rules in code: Project Owner → up to Deployer; Approver → Platform Admin only; individual (non-group) grants must have an expiry; self-approval always blocked; every grant, change, removal and expiry audited.

### 7.3 Agent-side enforcement (even if portal is compromised)
- Fixed command set. No generic "run" anywhere.
- Local config limits: environment, root paths, whether Managed is allowed.
- Path handling: canonicalize, then verify prefix is an allowed root; reject `..`, junctions/symlinks that leave the root, alternate data streams, reserved names.
- Protected data folders: the agent has a hard-coded rule "never write/delete under a path listed as DataFolder in the command" **and** keeps its own copy per target, so the portal cannot send a command with an empty list on a target that previously had one (agent refuses a list that shrinks without a local-side acknowledgment—see open question 9).
- Zip extraction is done by the agent too (zip-slip check again), not trusted from the portal.
- Hash check on the downloaded package before extraction.
- Health-check URL is restricted to the server itself.
- Folder ACLs: only `IIS AppPool\<name>` accounts, only Read / ReadAndExecute / Modify, only inside roots. Never FullControl, never other principals.

### 7.4 Optional: signed approvals
At approval, the portal creates a manifest (request id, package hash, target ids, mode, expiry) signed with a key held in Key Vault / HSM. Agent verifies the signature with a pinned public key. This stops "forge a deployment" by anyone who only has DB or SignalR access, but not an attacker who controls the portal process and its key access. Deferred unless you want it earlier.

### 7.5 Upload and package safety
- Max size (default 500 MB), enforced on session start and per chunk.
- Zip checks: no `..`, no absolute paths, no drive letters, no ADS (`:`), no reserved device names, no duplicate names that differ only by case, max entry count and max uncompressed size, and a compression-ratio limit (zip-bomb protection). These limits are not in the brief; I add them with configurable defaults.
- Blocked extensions: `.exe .bat .cmd .ps1 .msi` unless the component allows them.
- Defender scan; `Infected` or scan error blocks submission.
- Stored by hash; retention job in Hangfire; download = Platform Admin only and audited.
- Packages stored outside any web root.

### 7.6 Secrets
- Nothing secret in DB or config files. Key Vault (or DPAPI machine scope on-prem) for: SMTP password, signing key, Graph client secret/cert, SQL if not using integrated auth.
- Masking: a single `SecretMasker` with tests (connection string patterns, `password=`, `pwd=`, `secret`, `token`, `apikey`, long base64/hex values, JSON keys with those names). Used by the agent (before sending) and the portal (before rendering).

### 7.7 Audit
Logged: login, every role change, server add/approve/revoke, mode change, mapping, link, every IIS change (before/after), upload, scan result, submit, approve/reject, run start/step/end, rollback, package download, report exports, settings. Technical stack traces are for admins only; users see a short message and a correlation ID.

---

## 8. UI plan (MudBlazor only)

- Layout: `MudLayout` + `MudDrawer` (left nav), `MudAppBar` (user menu, theme toggle), light/dark via `MudThemeProvider`. Collapses to mini-drawer on tablet width.
- Lists: `MudDataGrid` with server-side paging/sort/filter (not loading all rows).
- Status chips: one `StatusChip` component and one color map (Succeeded green, Submitted/Pending amber, Failed red, RolledBack purple, Running blue, Scheduled indigo, Rejected/Expired/Cancelled grey, NeedsReapproval orange, Not-tested-on-Staging badge red outline).
- Wizard: `MudStepper`. Upload step uses a small JS module (fetch + chunk loop + progress callback); no `InputFile`.
- Live updates: `/hubs/ui` groups per request, per project, per server; approval queue per user.
- Destructive dialog: `TypeToConfirmDialog` reused everywhere.
- Diff viewer: virtualized list, red highlight for forced files, masked values shown as `••••`.

Screens are the ones in the brief. Phase 1 builds: Dashboard (partial), Servers, Server detail, Audit log (basic), Admin (basic).

---

## 9. Phases and testing

| Phase | Delivers | Tests |
|---|---|---|
| 1 Foundation | Solution, Entra login, platform roles, server add/register/approve, mTLS agent connect, heartbeat, discovery, discovery report, installer script v1 | Unit: access evaluator, token expiry, report queries. Integration: agent discovery against a real IIS test server |
| 2 Projects/onboarding | Projects, components, targets, uniqueness rules, mapping screen, role assignments + expiry + group resolution, reconciliation job, drift | Unit: link rules, grant rules. Integration: drift on rename/delete |
| 3 Deployments | Chunked upload, validation, AV, protected paths, diff, approvals, Promote, direct-to-prod, agent pipeline, rollback, live logs, email | Unit: state machine, diff, protected paths, masker, zip validator. Integration: full pipeline on a test site including failure at each step with restore |
| 4 IIS management | Sites, pools, apps, vdirs, folders/ACLs, bindings | Integration: each op twice (idempotency), before/after |
| 5 Extras | Templates, reports, polish. Later: Azure DevOps, bundles | — |

Test environment needed from you: one Windows Server with IIS (a throwaway VM) and a Windows CI runner. I cannot provide these.

---

## 10. Risk register (top items)

| Risk | Impact | Mitigation |
|---|---|---|
| Portal compromise → malicious deploy to all servers | Critical | Local agent limits, optional signed approvals, network isolation, audit hash chain, alert on unusual volume |
| Full Replace deletes unlisted user data | High | Section 0.6 gates |
| Mixed versions on load-balanced servers | Medium | Default all servers, reason required for one, Partial banner, roll back all |
| Agent holds local admin | High | Allow-list only, signed single-file build, file ACLs on agent folder, per-env accounts |
| DMZ servers need outbound access to internal portal | Medium | Needs firewall change from the network team; alternative is a mTLS-passthrough proxy in the DMZ. Start this conversation early |
| `Microsoft.Web.Administration` under .NET 10 self-contained single-file | Medium | Spike at start of Phase 1; fallback is non-single-file publish |
| Defender weak on archives | Medium | Treat as one layer; approval and hash remain the controls |
| Config-file masking misses a secret | Medium | Warning in UI; force overwrite is rare and approver-reviewed |
| Backup disk space | Medium | Preflight free-space check; keep N configurable; backups exclude data folders |
| Single portal node | Medium | Acceptable for v1; design avoids in-memory state |
| Unclear naming (legacy) | Low | Never rename existing; naming rule applies to new objects only |
| Clock skew breaks command expiry | Low | Heartbeat includes server time; warn on skew > 2 min |

---

## 11. Decisions I made so you don't have to (change if you disagree)

- Environments are a table, not an enum.
- Chunk size 8 MB, resumable, upload session expires in 24 h.
- Heartbeat 30 s; offline after 90 s.
- Request expiry 7 days; scheduled-run offline grace 60 min.
- Default backups kept: 5 per target. Default package retention: 90 days.
- Packages download to agents over HTTPS (Range), not through SignalR.
- Roles for groups are resolved from token claims for interactive use, and from Graph at execution time.
- Promote means: latest package with `Succeeded` on Staging for that component and not rolled back afterwards. Promote still needs Production approval. Package is re-scanned if the last scan is older than 7 days.
- Hangfire dashboard: Platform Admin only.

---

## 12. Open questions (need your answer before Phase 1)

1. **Agent hostname/port** (0.7): separate hostname on 443, or separate port 8443?
2. **Local-config model** (0.1): accept that environment/roots/Managed-allowed are fixed at install on the server, and the portal can only confirm them?
3. **Signed approvals** (0.2 / 7.4): include in Phase 3, or leave for later?
4. **Config-file diff** (0.3): allow the narrow "agent sends masked content for forced files" exception?
5. **Shared pools** (0.10): admin exception flow, and "one pool = one component"?
6. **Certificates** (0.11): bind existing only, no PFX upload?
7. **Approver grants**: the brief says Project Owners grant up to Deployer and only Platform Admin grants Approver on Production. Who grants Approver on Dev and Staging? I assume Platform Admin only. Correct?
8. **Direct to Production**: should a component be able to **require** Staging first (my `RequiresStagingBeforeProd` flag, default off)? You said "allowed", but some components will want it blocked.
9. **Protected-path changes**: who can shorten a component's protected list, and does it need approval? I propose Platform Admin only, audited, and the agent stores its own copy (7.3).
10. **Entra or AD?** Group claims and Graph lookup differ. Which is it, and can the app get group-read permission?
11. **Portal host**: Windows Server with Defender confirmed? File share or Blob for packages?
12. **Email**: SMTP relay available? Which sender address?
13. **Test infrastructure**: can you provide a Windows IIS test VM and a Windows build runner (GitHub Actions `windows-latest` works for unit tests; IIS integration needs a self-hosted runner or a runner where IIS is enabled)?
14. **Site naming default**: `<project>-<component>-<env>` and `D:\Websites\<project>\<component>` as given?
15. **Production approvals**: is a requester who is also the only Approver on Dev blocked as in the brief, with the effect that small teams may be unable to approve anything? Confirm each project will have at least two Approvers per environment.

---

## 13. Next step

Answer section 12 (short answers are fine; "your default" is acceptable). Then I start Phase 1 and stop for your review at the end of it.
