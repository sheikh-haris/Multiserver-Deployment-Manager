# Phase 4b review notes: sites, applications, folders, pool creation

Built from your answers: **A** no delete, **B** Modify access allowed, **C** project Owners can create, **D** host name + port, IP always `*`.

## What you can do now

Server page → **Sites, bindings and folders** (`/servers/<id>/iis`). Project Owners reach it from the project page ("IIS on <server>").

| Operation | Notes |
|---|---|
| Create app pool | IIS defaults; edit the settings afterwards on the app pools page |
| Create site | Name, folder, app pool, one or more HTTP bindings (host name + port). **Created stopped.** |
| Start / Stop a site | |
| Edit HTTP bindings | Replaces only the HTTP bindings. HTTPS bindings and certificates are untouched (tested on real IIS). |
| Create application / virtual directory | Folder, pool; must not already exist as a path in the site |
| Create folder | Only inside an allowed root |
| Give a pool access to a folder | Account `IIS AppPool\<pool>`; Read, Read and execute, or Modify. Modify needs a written reason and has its own audit action (`Iis.GrantFolderAccess.Modify`). |
| **Delete anything** | **Not available** (your answer A). Folders and files are never deleted by the portal. |

## Rules (enforced on the agent, repeated in the portal)

- **Gates:** Server Operator for the environment or Platform Admin, server Managed in the portal AND allowed locally, agent online. The agent refuses otherwise.
- **Production:** every change except Start needs a reason and a second Server Operator or Platform Admin (same Pool approvals screen). Approving runs it.
- **Roots only:** every folder must be strictly inside an allowed root (not the root itself, no `..`, no UNC, no drive letter alone). Existing links (junctions, symlinks) in the path are refused, so a path inside a root cannot lead outside it (tested).
- **Names and bindings:** host name required (a binding without one would answer for every name), no wildcards, port 80 to 65535, not 443, no duplicates. A binding already used by another site is refused. A site name or folder already used by IIS is refused. Two things must never share one folder.
- **Stale protection:** editing bindings sends what you saw. If someone changed them since, nothing changes.
- **Folder access:** only an existing pool's account, only on an existing folder inside a root, never lowers existing access, never FullControl, never another account. The permission is read back to confirm.
- **Audit:** every attempt with before and after. A new site/pool/folder is also recorded in an ownership ledger (who, for which project).

## Project Owners (your answer C)

An Owner can, **in Dev and Staging only**, on a server where their project **already has a deployment target**:
- create app pools, sites, applications, virtual directories and folders for their project;
- start, stop and edit bindings of **their own** sites (their targets' sites and sites they created);
- give **their own** pools Read or Read-and-execute on folders inside their own folders (never Modify).

Extra limits so an Owner cannot reach other people's data:
- The folder of a new site, application or virtual directory **must be new**. The agent refuses an existing folder, so a new site can never expose a folder that was already there.
- A new folder cannot be inside a folder that belongs to another project.
- Only pools of their own project can be used for their sites.
- **Chicken and egg:** the first site on a server for a new project must be created by a Server Operator (then mapped to the component). After that the Owner can add more.

## Verified by

| Area | How |
|---|---|
| Rules and agent handler (gates, roots, links, names, bindings, conflicts, stale edits, access, idempotence) | 17 new agent tests |
| Portal (roles, ownership, Production approval flow, Modify reason and audit action, ledger, discovery refresh, page for owners) | 11 new web tests |
| Real browser, real portal, real agent (sample IIS) | `tests/Smoke/run.sh`: operator creates a site (folder created in the root, starts stopped, can be started), outside folder refused, a project Owner creates an app pool from the project page |
| Real IIS | New integration tests: pool with settings, site created stopped, bindings replaced while HTTPS and its certificate survive, applications and virtual directories, folder ACL accumulation and inheritance, handler against real IIS. Run by CI on Windows. |

## Decisions I made that you should check

1. **Owners cannot grant Modify** (you said yes to Modify; I kept it with Operators and Platform Admins). Say if Owners should be able to for their own folders.
2. **Folder changes of linked sites** (changing a site's folder or pool) are not offered at all. Only new things and bindings. Changing a linked target's folder would make deployment targets Drifted.
3. **Created sites show up in discovery** (the portal asks for a discovery right after) and then appear in the onboarding list, where you map them to a component.
4. **A new pool uses IIS defaults.** Settings come from the edit screen.
5. Contract version is now 4: **agents must be upgraded again.**
