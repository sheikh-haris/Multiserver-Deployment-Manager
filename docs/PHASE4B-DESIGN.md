# Phase 4b design (draft for your approval): sites, applications, folders, pool create/delete

Phase 3 (deployments) and Phase 4a (app pools) are approved. This is the rest of "IIS management" from the original brief.
Nothing here is built yet. Defaults are chosen so you can answer "OK" and only change what you disagree with.

## What it would add

| Area | Operations |
|---|---|
| Sites | Create (name, folder, one or more HTTP bindings, pool); start / stop; edit HTTP bindings; delete (IIS configuration only) |
| Applications and virtual directories | Create, edit (folder, pool), delete |
| App pools | Create (with the same fixed settings as today), delete (only if nothing uses it) |
| Folders | Create a folder inside an allowed root; grant `IIS AppPool\<pool>` access to a folder inside an allowed root |

## Defaults I propose

1. **Same gates as pools:** Server Operator for the environment or Platform Admin; server Managed in the portal *and* allowed locally on the server; agent enforces everything again.
2. **Production needs a second person** for every create, edit, stop and delete (the same request/approve screen as pool approvals). Start is direct.
3. **Deleting never deletes files.** Deleting a site, application or virtual directory removes the IIS configuration only. Folders and content stay. Refused while a deployment target is linked to it (unlink first), and refused for the Default Web Site and for anything the agent cannot show was created by the portal? (see question A).
4. **Everything is limited to the agent's allowed roots.** Site and application folders must be inside a root (for example `D:\Websites`). Folders are created only inside roots. No `..`, no drive roots, no UNC paths, no reparse points.
5. **HTTP bindings only** (decision D3). HTTPS stays in IIS Manager, existing HTTPS bindings are left alone when you edit HTTP ones. Port 80/443/8080-style conflicts with other sites are checked before sending.
6. **Folder access (ACL):** only the account `IIS AppPool\<name>` of a pool that exists on that server; only Read, ReadAndExecute or Modify; only on folders inside roots; never FullControl, never other accounts, never inheritance changes above the folder. Read+Execute is the default. **Modify** needs a reason and is audited separately.
7. **Everything is audited with before and after**, and can be stale-checked the same way pool edits are (the agent refuses if the live value differs from what you saw).
8. **A new site starts stopped**, so it can be checked before it takes traffic. You press Start.

## Questions I need you to answer

A. **Delete:** allowed from the portal (with the Production approval) or do you want deletes left to IIS Manager? If allowed, should the portal only delete things **it created** (safest), or anything non-default?
B. **Folder ACL / Modify:** do you want the portal to be able to grant Modify at all? Without it, apps that write to a folder (uploads) need someone to set that permission on the server.
C. **Who may create sites in Dev and Staging:** Server Operator only (my default), or also project Owners for their own project's servers?
D. **Bindings:** do you need host-header sites only (e.g. `app.example.local:80`), or also IP-based and non-standard ports (8080)? My default: host name plus port, any port from 80 to 65535, IP always `*`.

## Cost and risk

About the same size as Phase 3. The risky parts are delete, folder creation and ACL. Those are the reasons for the root limits, "IIS configuration only" deletes and the account/permission allow-lists above.
