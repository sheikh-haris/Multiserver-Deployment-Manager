# Phase 5 – Extras

Three things: a better Promote flow, CSV reports, and site templates. Azure DevOps integration and release bundles are still "later" and are NOT in this phase.
No agent change: the contract version stays at 4.

## 1. Promote flow

- **Ready to promote** tab on the Deployments page: for each component, the newest package that succeeded on Staging and is not yet in Production. Shown only to people who may deploy to Production and only when the project has an active Production target. A package with an open request is not shown again.
- **Promote to Production** button on the detail page of a succeeded Staging deployment.
- **Versions** tab on the project page: current version per component and environment (the last deployment that succeeded and was not rolled back).
- Rule unchanged: "promote" means the same package (same SHA-256) that already succeeded on Staging. Production approval rules are unchanged.

## 2. Reports (CSV)

On the Reports page (`/reports/deployments`):

| File | Who | Content |
|---|---|---|
| `/export/deployments.csv` | people who can see deployments | requests with who, when, versions, result, approvals |
| `/export/inventory.csv` | ViewServers / ViewDiscoveryReport | servers, sites, pools, folders |
| `/export/access.csv` | ViewAudit / ManageRoles | who has which role where |

- Cells that start with `=`, `+`, `-`, `@` are neutralized (Excel formula injection).
- Every export is written to the audit log (`Report.Export`).

## 3. Site templates

A template is a reusable layout: pool settings, extra folders, applications, virtual directories, folder access. All folders are **relative** to the site folder chosen when the template is used.

- A template never holds host names, servers, accounts or passwords. Captured pools with a custom account become ApplicationPoolIdentity.
- **Manage** (create, capture from an existing site, edit, remove): Server Operator or Platform Admin. Page: *Site templates*.
- **Use**: "Create site from template…" on a server's IIS page. Operators on that environment, and project Owners (own project, Dev/Staging, same rules as manual creation: new folders only).
- Applying = the same single operations as by hand, in this order: pool, site (starts stopped), folders, applications, virtual directories, folder access. Each step goes through all normal checks and audit.
- **Dev/Staging:** steps run in order and stop at the first failure. Nothing is undone (there is no delete). The message says which step failed.
- **Production:** the whole list is ONE request. The second person sees every step and approves them together. Requester cannot approve their own. Expires after 24 hours as before.
- **Modify access** in a template: needs a written reason; project Owners cannot apply such a template.
- Removing a template does not touch sites made from it.
- Audit: `Template.Create/Update/Remove/Apply`, plus the normal `Iis.*` lines for every step.

## Known limits

- A failed Dev/Staging batch leaves the earlier steps in place. Fix the cause and finish by hand, or reuse a new site name; there is no automatic clean-up by design.
- Templates are not versioned. Editing one changes what the next use creates, not existing sites.
- Capture only takes applications and virtual directories that are inside the site folder; others are counted and skipped (the audit line says how many).
- Real-IIS CI covers each step type; the template batch itself is covered by unit tests and the browser smoke test (sample IIS data).

## Tests

Core: `SiteTemplateTests` (rules, planner order). Web: `SiteTemplateServiceTests` (permissions, apply, stop at failure, Production single request with second approver, owner limits, capture), `PromoteTests`, `ReportTests`. Smoke: template created in the UI and a site created from it.
