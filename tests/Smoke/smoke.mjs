import { createRequire } from 'module';
import fs from 'fs';
const { chromium } = createRequire(import.meta.url)('playwright');
const { PORTAL, PID, CID, SID, W, OUT, SITE_PORT } = process.env;
const must = (c, m) => { if (!c) { throw new Error('ASSERT: ' + m); } console.log('ok -', m); };
const read = f => fs.readFileSync(`${W}/websites/shop/${f}`, 'utf8').trim();
const browser = await chromium.launch({ executablePath: process.env.CHROME || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args: ['--no-sandbox'] });
async function person(key) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400, height: 1000 } });
  const page = await ctx.newPage();
  page.on('pageerror', e => console.log('PAGE ERROR', e.message));
  await page.goto(`${PORTAL}/dev-login/${key}?returnUrl=/`);
  return page;
}
const shot = (page, name) => page.screenshot({ path: `${OUT}/${name}.png`, fullPage: true });
async function submit(page, zip, source) {
  await page.goto(`${PORTAL}/projects/${PID}`);
  await page.waitForLoadState('networkidle');
  await page.getByRole('button', { name: /Deploy to Dev/ }).first().click();
  await page.waitForURL(/deployments\/new/);
  await page.getByText('Upload a package').waitFor();
  await page.setInputFiles('#pkg-file', `${W}/pkg/${zip}`);
  await page.getByRole('button', { name: 'Upload', exact: true }).click();
  return page;
}

const page0 = await person('admin');
const dep = await person('deployer');

// ---------- 1. a dangerous package is refused with the reasons ----------
await submit(dep, 'evil.zip');
await dep.getByText(/refused|problem/i).first().waitFor({ timeout: 30000 });
await shot(dep, '01-evil-refused');
must(await dep.getByText(/escape|\.\./).count() > 0, 'zip-slip package is refused and the path is shown');

// ---------- 2. good package v1: upload, review, submit ----------
await submit(dep, 'v1.zip');
await dep.getByText('Package is ready.').waitFor({ timeout: 60000 });
await dep.getByRole('button', { name: 'Next' }).click();
await dep.getByLabel(/Source reference/).fill('PR-101 first release');
await dep.getByRole('button', { name: 'Review changes' }).click();
await dep.getByText(/added/).first().waitFor({ timeout: 60000 });
await shot(dep, '02-review');
must(await dep.getByText('config kept').count() > 0, 'review shows the existing web.config is kept');
await dep.getByRole('button', { name: 'Submit for approval' }).click();
await dep.waitForURL(/deployments\/[0-9a-f-]{36}$/);
const id1 = dep.url().split('/').pop();
await dep.getByText('Submitted').first().waitFor();
await shot(dep, '03-submitted');
must(await dep.getByRole('button', { name: 'Approve' }).count() === 0, 'the requester has no Approve button');

// ---------- 3. approver approves; agent runs it ----------
const appr = await person('approver');
await appr.goto(`${PORTAL}/deployments`);
await appr.getByText(/Waiting for my approval \(1\)/).waitFor({ timeout: 20000 });
await appr.goto(`${PORTAL}/deployments/${id1}`); await appr.waitForLoadState('networkidle');
await shot(appr, '04-approver-view');
await appr.getByRole('button', { name: 'Approve', exact: true }).click();
for (let i = 0; i < 60; i++) { const s = await (await page0.request.get(`${PORTAL}/dev/e2e/request?id=${id1}`)).json(); if (s.status === 'Succeeded') break; if (['Failed','RolledBack','NeedsReapproval'].includes(s.status)) throw new Error('unexpected ' + JSON.stringify(s)); await new Promise(r => setTimeout(r, 1000)); }
const s1 = await (await page0.request.get(`${PORTAL}/dev/e2e/request?id=${id1}`)).json();
must(s1.status === 'Succeeded', 'v1 deployment succeeded: ' + JSON.stringify(s1.runs[0]));
must(read('index.html').includes('version 1'), 'index.html replaced');
must(read('app.dll') === 'dll-v1', 'app.dll added');
must(read('web.config') === 'original-config', 'protected web.config was kept');
must(read('keep.txt') === 'user-data', 'unrelated file untouched');
await appr.reload(); await appr.getByText('Succeeded').first().waitFor();
await shot(appr, '05-succeeded');
must(await appr.getByText(/Validate|Backup|Copy/).count() > 0, 'run log is shown');

// ---------- 4. broken package: health check fails -> automatic rollback ----------
await submit(dep, 'v2-broken.zip');
await dep.getByText('Package is ready.').waitFor({ timeout: 60000 });
await dep.getByRole('button', { name: 'Next' }).click();
await dep.getByLabel(/Source reference/).fill('PR-102 broken');
await dep.getByRole('button', { name: 'Review changes' }).click();
await dep.getByText(/added|replaced/).first().waitFor({ timeout: 60000 });
await dep.getByRole('button', { name: 'Submit for approval' }).click();
await dep.waitForURL(/deployments\/[0-9a-f-]{36}$/);
const id2 = dep.url().split('/').pop();
await appr.goto(`${PORTAL}/deployments/${id2}`); await appr.waitForLoadState('networkidle');
await appr.getByRole('button', { name: 'Approve', exact: true }).click();
let s2;
for (let i = 0; i < 90; i++) { s2 = await (await page0.request.get(`${PORTAL}/dev/e2e/request?id=${id2}`)).json(); if (!['Submitted','Approved','Running'].includes(s2.status)) break; await new Promise(r => setTimeout(r, 1000)); }
must(s2.status === 'RolledBack', 'broken package ended RolledBack: ' + JSON.stringify(s2));
must(read('index.html').includes('version 1'), 'files restored to version 1 after the failed health check');
must(!fs.existsSync(`${W}/websites/shop/app_offline.htm`), 'app_offline.htm removed after rollback');
await appr.reload(); await appr.getByText('RolledBack').first().waitFor();
await shot(appr, '06-rolledback');

// ---------- 5. v3, then manual rollback ----------
await submit(dep, 'v3.zip');
await dep.getByText('Package is ready.').waitFor({ timeout: 60000 });
await dep.getByRole('button', { name: 'Next' }).click();
await dep.getByLabel(/Source reference/).fill('PR-103');
await dep.getByRole('button', { name: 'Review changes' }).click();
await dep.getByText(/added|replaced/).first().waitFor({ timeout: 60000 });
await dep.getByRole('button', { name: 'Submit for approval' }).click();
await dep.waitForURL(/deployments\/[0-9a-f-]{36}$/);
const id3 = dep.url().split('/').pop();
await appr.goto(`${PORTAL}/deployments/${id3}`); await appr.waitForLoadState('networkidle');
await appr.getByRole('button', { name: 'Approve', exact: true }).click();
for (let i = 0; i < 60; i++) { const s = await (await page0.request.get(`${PORTAL}/dev/e2e/request?id=${id3}`)).json(); if (s.status === 'Succeeded') break; await new Promise(r => setTimeout(r, 1000)); }
must(read('index.html').includes('version 3'), 'v3 is live');
await dep.goto(`${PORTAL}/deployments/${id3}`); await dep.waitForLoadState('networkidle');
await dep.getByRole('button', { name: /Roll back/ }).click();
await dep.getByLabel('Reason').fill('v3 has a bug');
await dep.getByRole('button', { name: 'Request rollback' }).click();
await dep.waitForURL(url => /deployments\/[0-9a-f-]{36}$/.test(url.toString()) && !url.toString().endsWith(id3));
const idRb = dep.url().split('/').pop();
for (let i = 0; i < 60; i++) { const s = await (await page0.request.get(`${PORTAL}/dev/e2e/request?id=${idRb}`)).json(); if (s.status === 'Succeeded') break; await new Promise(r => setTimeout(r, 1000)); }
must(read('index.html').includes('version 1'), 'manual rollback restored version 1');
await dep.reload(); await shot(dep, '07-manual-rollback');

// ---------- 6. app pools: read-only first, then Managed ----------
const op = await person('operator');                       // Server Operator for Dev
await op.goto(`${PORTAL}/servers/${SID}/pools`); await op.waitForLoadState('networkidle');
await op.getByText('ShopPool').first().waitFor();
await shot(op, '08-pools-readonly');
must(await op.getByText(/ReadOnly/).count() > 0, 'a ReadOnly server explains why pools cannot be changed');
must(await op.getByRole('button', { name: 'Stop', exact: true }).isDisabled(), 'Stop is disabled on a ReadOnly server');

await page0.request.post(`${PORTAL}/dev/e2e/mode?serverId=${SID}&mode=Managed`);
await op.reload(); await op.waitForLoadState('networkidle');
const row = op.locator('tr', { hasText: 'ShopPool' });
await row.locator('input[type=checkbox]').check();
await op.getByRole('button', { name: 'Stop', exact: true }).click();
await op.locator('.mud-dialog-container').getByRole('button', { name: /^stop$/i }).click();          // confirm in the dialog
await op.getByText(/ShopPool: Started → Stopped/).waitFor({ timeout: 30000 });
must(await row.getByText('Stopped').count() > 0, 'the pool shows Stopped after Stop');
await shot(op, '09-pool-stopped');
await op.getByRole('button', { name: 'Start', exact: true }).click();
await op.getByText(/ShopPool: Stopped → Started/).waitFor({ timeout: 30000 });
await op.getByRole('button', { name: 'Recycle', exact: true }).click();
await op.locator('.mud-dialog-container').getByRole('button', { name: 'Recycle', exact: true }).click();
await op.getByText(/ShopPool: Already Started/).waitFor({ timeout: 30000 });
must(true, 'start and recycle work');

await row.getByRole('button', { name: 'Edit' }).click();
await op.getByLabel('Queue length').fill('2500'); await op.getByLabel('Queue length').press('Tab');
await op.getByRole('button', { name: 'Save changes' }).click();
await op.getByText(/ShopPool saved/).waitFor({ timeout: 30000 });
await shot(op, '10-pool-edited');
await row.getByRole('button', { name: 'Edit' }).click();
must(await op.getByLabel('Queue length').inputValue() === '2500', 'the edited value is shown when the pool is opened again');
await op.getByRole('button', { name: 'Cancel' }).click();

// a project Deployer can recycle the pool of their own application, from the project page
await dep.goto(`${PORTAL}/projects/${PID}`); await dep.waitForLoadState('networkidle');
await dep.getByRole('button', { name: /Recycle pool/ }).click();
await dep.locator('.mud-dialog-container').getByRole('button', { name: /^recycle$/i }).click();
await dep.getByText(/Recycled ShopPool/).waitFor({ timeout: 30000 });
must(true, 'a Deployer can recycle their own pool from the project page');
await shot(dep, '11-deployer-recycle');
await appr.goto(`${PORTAL}/projects/${PID}`); await appr.waitForLoadState('networkidle');
must(await appr.getByRole('button', { name: /Recycle pool/ }).count() === 0, 'an Approver (no Deployer role) has no Recycle button');

// ---------- 6b. IIS management: an operator creates a site; a project Owner creates their own ----------
await op.goto(`${PORTAL}/servers/${SID}/iis`); await op.waitForLoadState('networkidle');
await op.getByRole('button', { name: 'Create site' }).click();
const dlg = op.locator('.mud-dialog-container');
await dlg.getByLabel('Site name').fill('blog');
await dlg.getByLabel('Folder on the server').fill(`${W}/websites/blog`);
await dlg.getByLabel('Host name').fill('blog.test');
await dlg.getByLabel('Port').fill('8090');
await dlg.getByRole('button', { name: 'Do it' }).click();
await op.getByText(/Site created \(stopped\)/).waitFor({ timeout: 30000 });
must(fs.existsSync(`${W}/websites/blog`), 'the site folder was created inside the allowed root');
const blogRow = op.locator('tr', { hasText: 'blog' });
await blogRow.getByText('Stopped').waitFor({ timeout: 30000 });
await blogRow.locator('button').last().click();
await op.getByRole('menuitem', { name: 'Start' }).click();
await blogRow.getByText('Started').waitFor({ timeout: 30000 });
await shot(op, '12-iis-site-created');
must(true, 'the new site is listed, starts stopped, and can be started');

// a folder outside the root is refused by the agent
await op.getByRole('button', { name: 'Create folder' }).click();
await op.locator('.mud-dialog-container').getByLabel('New folder').fill('/etc/evil');
await op.locator('.mud-dialog-container').getByRole('button', { name: 'Do it' }).click();
await op.getByText(/allowed root/).waitFor({ timeout: 30000 });
must(!fs.existsSync('/etc/evil'), 'a folder outside the allowed root is refused');

await page0.request.post(`${PORTAL}/dev/e2e/grant?persona=approver2&projectId=${PID}&role=ProjectOwner&env=Dev`);
const own = await person('approver2');
await own.goto(`${PORTAL}/projects/${PID}`); await own.waitForLoadState('networkidle');
await own.getByRole('button', { name: /IIS on SMOKE01/ }).click();
await own.waitForURL(/\/iis\?project=/); await own.waitForLoadState('networkidle');
await own.getByRole('button', { name: 'Create app pool' }).click();
await own.locator('.mud-dialog-container').getByLabel('App pool name').fill('ownpool');
await own.locator('.mud-dialog-container').getByRole('button', { name: 'Do it' }).click();
await own.getByText(/App pool created/).waitFor({ timeout: 30000 });
await shot(own, '13-owner-iis');
must(true, 'a project Owner creates an app pool for their project');

// ---------- 7. pages that must load ----------
for (const p of ['/deployments', '/', `/projects/${PID}`, '/reports/deployments', '/pools/requests', `/servers/${SID}/pools`]) {
  await page0.goto(`${PORTAL}${p}`); await page0.waitForLoadState('networkidle'); await shot(page0, 'page' + p.replace(/\W+/g, '-'));
}
await browser.close();
console.log('all browser checks passed');
