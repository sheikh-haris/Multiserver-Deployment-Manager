# Phase 4a: app pool management

You asked for: start, stop and recycle app pools on a chosen server by selecting them in the portal, and editing app pool configuration.
This is the first part of Phase 4 (IIS management). Sites, bindings, folders and certificates stay for later parts.

## What you can do now

Server page → **Manage app pools** (also reachable at `/servers/<id>/pools`).

| Action | How |
|---|---|
| Start / Stop / Recycle | Tick one or more pools, press the button. A confirmation shows what is affected (the components and sites that use those pools). In **Production** you must type `PROD` for Stop, Recycle and edits. Start needs no confirmation. |
| Edit settings | **Edit** on one pool: .NET CLR version, pipeline mode, identity (built-in accounts only), start mode, idle time-out, queue length, recycle interval, recycle times, private memory limit, 32-bit, load user profile, rapid-fail protection and its crash limit. Only fields you change are sent. |
| See who uses a pool | Each row lists the components (projects) and IIS sites that use the pool. |

## Rules (all enforced on the agent too, and tested)

- **Who**: Server Operator for that environment, or Platform Admin. Auditor can look, not change.
- **Server must be Managed**: the portal mode must be Managed **and** the agent must have been installed with Managed allowed. The agent refuses otherwise, even if the portal asks.
- **Fixed list of settings.** Nothing else can be read or written. There is no password and no custom account in the portal, in either direction.
- **Identity**: only ApplicationPoolIdentity, NetworkService or LocalService. LocalSystem and custom accounts are refused (a custom account needs a password, which this system never handles). A pool that already runs as a custom account can still be edited for other settings: its account and password are left untouched (tested on real IIS).
- **No blind overwrite**: the edit carries the values you saw. If someone changed the pool since (for example in IIS Manager), the agent refuses and nothing changes.
- **Honest results**: a stopped pool cannot be recycled (it is not silently started). After an edit the agent reads the pool back and fails if IIS does not show the new value.
- **One operation at a time** per server (IIS configuration commits collide otherwise). Maximum 50 pools per click.
- **Audit**: every attempt, also denied and failed, with before and after state. For edits, only the fields that were changed are recorded.
- **No false drift**: the portal updates its stored snapshot with what the agent reports, so your own change is not shown on the Drift page.

## Also changed in this round

- **Nested sites and applications (your answer):** a sub-application or virtual directory in a sub-folder of its site is normal and is now **allowed**. Only two items pointing at *exactly the same folder* are blocked. To keep deployments safe, while deploying to the parent, everything IIS serves from a folder inside it (other applications, virtual directories) is treated as a protected data folder: never overwritten, never deleted, never in the backup. The agent reads this from IIS itself, the portal from discovery. The review screen shows those files as "data skipped".
- **Project Owners can see the onboarding list (your answer):** no change, as built.
- Contract version is now 3: agents must be upgraded again.
- Fixed in the Windows tests: IIS integration tests run one at a time (they share one IIS configuration file); a file loaded by a running app is readable but not replaceable.

## Verified by

| Area | How |
|---|---|
| Pool rules (ranges, identities, times, conflicts, "effective" changes) | 29 new Core tests |
| Agent handler (ReadOnly gate, envelope checks, stale edit, dangerous identity, idempotence) + nested-folder deployment and rollback | 10 new agent tests |
| Portal service (roles, ReadOnly, offline, Prod confirmation, several pools, failures, audit before/after, no drift, wrong-server answers) | 12 new web tests |
| Real browser, real portal, real agent (sample pools) | `tests/Smoke/run.sh`: ReadOnly explains itself and disables buttons; switch to Managed; stop, start, recycle, edit, reopen shows the saved value |
| Real IIS | New integration tests (read without passwords, round trip of every setting, custom account and password survive an edit, stop/start/recycle, handler rules). Run by CI on Windows; see the result in the notes below. |

## Decisions I made that you should check

1. **No approval workflow for pool actions.** One operator acts, with a typed confirmation in Production, and everything is audited. If you want a second person for Production stops or edits, say so; it reuses the approval machinery from Phase 3.
2. **Project Deployers cannot recycle their own pool.** Only Server Operators and Platform Admins. Many teams want developers to recycle their own app; it is a small change (new project permission) if you want it.
3. **Pool names must come from the last discovery.** Creating or deleting pools comes with the rest of Phase 4.
4. **Stopping a pool that production sites use is allowed** (with a warning and confirmation). There is no "protected pool" list yet.
