// End-to-end tests of the till screen: a real hub, a real browser (Chromium via Playwright).
// Run from the repository root after a Release build:  node --test tests/ui/
// Needs Node 22+ and Playwright with Chromium (npm install in tests/ui, then npx playwright install chromium).
const { test, before, after, afterEach } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright');

const repo = path.resolve(__dirname, '..', '..');
const hubDir = path.join(repo, 'src', 'Pos.Hub');
const dll = path.join(hubDir, 'bin', 'Release', 'net10.0', 'Pos.Hub.dll');
const dotnet = process.env.DOTNET ?? (fs.existsSync(path.join(os.homedir(), '.dotnet', 'dotnet')) ? path.join(os.homedir(), '.dotnet', 'dotnet') : 'dotnet');
const shots = process.env.POS_UI_SHOTS; // folder for screenshots, if set

let hub;
let base;
let data;
let browser;
let shop;

async function startHub() {
  data = fs.mkdtempSync(path.join(os.tmpdir(), 'pos-ui-'));
  const port = 5200 + Math.floor(Math.random() * 700);
  base = `http://localhost:${port}`;
  hub = spawn(dotnet, [dll,
    `--Hub:LocalHttpPort=${port}`, '--Hub:NetworkHttpsPort=0',
    `--Hub:DatabasePath=${path.join(data, 'data', 'hub.db')}`,
    `--Backup:Folder=${path.join(data, 'data', 'backups')}`,
    `--Printing:Receipt:Folder=${path.join(data, 'prints', 'receipts')}`,
    `--Printing:Kitchen:Folder=${path.join(data, 'prints', 'kitchen')}`,
  ], { cwd: hubDir, stdio: 'ignore' });
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch(`${base}/api/health`)).ok) return; } catch { /* starting */ }
    await new Promise(r => setTimeout(r, 250));
  }
  throw new Error('Hub did not start');
}

/** Calls the hub directly (for setting up staff and checking results). */
async function call(method, url, who, body, extra = {}) {
  const headers = { 'Content-Type': 'application/json', ...extra };
  if (who?.device) { headers['X-Device-Id'] = who.device.deviceId; headers['X-Device-Key'] = who.device.deviceKey; }
  if (who?.token) headers.Authorization = `Bearer ${who.token}`;
  const res = await fetch(base + url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${url}: ${res.status} ${text}`);
  return text ? JSON.parse(text) : null;
}

/** Owner, a cashier and a manager on a shop with the sample menu; two registered tills. */
async function setUpShop() {
  const setup = await call('POST', '/api/setup', null, { ownerName: 'Olivia Owner', pin: '4826', deviceName: 'Till 1' });
  const owner = { device: setup.device, token: setup.session.token };
  await call('POST', '/api/staff', owner, { name: 'Cara Cashier', role: 'Cashier', pin: '5931' });
  await call('POST', '/api/staff', owner, { name: 'Max Manager', role: 'Manager', pin: '7402' });
  await call('POST', '/api/menu/sample', owner);
  const { code } = await call('POST', '/api/devices/pairing-code', owner);
  const till2 = await call('POST', '/api/devices/pair', null, { code, name: 'Till 2', type: 'Till' });
  return { owner, till1: { ...setup.device, name: 'Till 1' }, till2: { ...till2, name: 'Till 2' } };
}

/** A browser page on a registered till, at the PIN screen. */
async function tillPage(device, viewport = { width: 1366, height: 768 }) {
  const context = await browser.newContext({ viewport });
  await context.addInitScript(d => {
    if (!localStorage.getItem('pos.device')) localStorage.setItem('pos.device', JSON.stringify(d));
  }, device);
  const page = await context.newPage();
  page.errors = [];
  page.on('pageerror', e => page.errors.push(e.message));
  // Script errors only; the browser also logs refused requests (e.g. a wrong PIN), which are expected.
  page.on('console', m => { if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) page.errors.push(m.text()); });
  await page.goto(`${base}/pos/`);
  open.add(page);
  return page;
}

async function pin(page, digits) {
  for (const d of digits) await page.locator('.keypad .key', { hasText: new RegExp(`^${d}$`) }).last().click();
}

async function login(page, digits) {
  await page.getByText('Enter your PIN').waitFor();
  await pin(page, digits);
  await page.getByRole('button', { name: 'Log in' }).click();
  await page.locator('.ticket').waitFor();
}

const top = page => page.locator('.dialog').last();
const shot = (page, name) => shots && page.screenshot({ path: path.join(shots, `${name}.png`) });
const open = new Set();
afterEach(async t => {
  // On failure, keep a picture of every open till screen.
  if (shots && t.passed === false) for (const page of open) await page.screenshot({ path: path.join(shots, `fail-${t.name.slice(0, 30)}-${[...open].indexOf(page)}.png`) }).catch(() => {});
  open.clear();
});

async function pick(page, category, item) {
  await page.locator('.cats').getByRole('button', { name: category, exact: true }).click();
  await page.locator('.items').getByRole('button', { name: new RegExp(item) }).click();
}

async function ordersToday() {
  return call('GET', '/api/orders', { device: shop.owner.device, token: shop.owner.token });
}

before(async () => {
  if (shots) fs.mkdirSync(shots, { recursive: true });
  await startHub();
  shop = await setUpShop();
  browser = await chromium.launch();
});

after(async () => {
  await browser?.close();
  hub?.kill();
  fs.rmSync(data, { recursive: true, force: true });
});

test('first-time set-up screen appears only on a fresh hub, on the hub PC', async () => {
  // This hub is set up, so a new browser is asked to register with a code instead.
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(`${base}/pos/`);
  await page.getByText('Register this till').waitFor();
  await page.getByLabel('Code').fill('000000');
  await page.getByRole('button', { name: 'Register' }).click();
  await page.getByText('That code is wrong or has expired.').waitFor();
  await context.close();
});

test('wrong PIN is refused; the right PIN logs in', async () => {
  const page = await tillPage(shop.till1);
  await pin(page, '1111');
  await page.getByRole('button', { name: 'Log in' }).click();
  await page.getByText('Wrong PIN.').waitFor();
  await pin(page, '5931');
  await page.getByRole('button', { name: 'Log in' }).click();
  await page.getByText('Cara Cashier').waitFor();
  await page.getByRole('button', { name: 'Log out' }).click();
  await page.getByText('Enter your PIN').waitFor();
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('cashier sends an order, adds to it, removes a kitchen item with a manager PIN, takes card and cash', async () => {
  const page = await tillPage(shop.till1);
  await login(page, '5931');

  // Kebab needs a sauce before it can be added.
  await pick(page, 'Kebabs', 'Doner kebab');
  await top(page).getByRole('button', { name: /Regular/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await top(page).getByText('Sauce: choose at least 1.').waitFor();
  await top(page).getByRole('button', { name: /Garlic/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await pick(page, 'Sides', 'Cheesy chips');
  await top(page).getByRole('button', { name: /Large/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await page.locator('.total-total', { hasText: '£12.49' }).waitFor(); // 7.99 + 4.50
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.getByText(/Order \d+ sent to the kitchen/).waitFor();
  const number = (await ordersToday()).at(-1).number;

  // Open it again and add two cans.
  await page.getByRole('button', { name: /^Orders/ }).click();
  await top(page).getByRole('button', { name: new RegExp(`^${number}`) }).click();
  await pick(page, 'Drinks', 'Can 330ml');
  await page.locator('.items').getByRole('button', { name: /Can 330ml/ }).click();
  await page.locator('.total-due', { hasText: '£2.40' }).waitFor();
  await shot(page, 'ui-add-to-order');
  await page.getByRole('button', { name: 'Send new' }).click();
  await page.getByText(/sent to the kitchen/).last().waitFor();

  // Remove the chips: already in the kitchen, so a manager must approve.
  await page.getByRole('button', { name: /^Orders/ }).click();
  await top(page).getByRole('button', { name: new RegExp(`^${number}`) }).click();
  await page.locator('.line', { hasText: 'Cheesy chips' }).click();
  await page.getByRole('button', { name: 'Remove item' }).click();
  await top(page).getByRole('button', { name: 'Entered by mistake' }).click();
  await top(page).getByText('Manager, enter your PIN.').waitFor();
  await pin(page, '5931'); // a cashier cannot approve
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await page.getByText("That person's role cannot approve this.").waitFor();
  assert.equal(await page.locator('.dialog').count(), 0);
  await page.getByRole('button', { name: 'Remove item' }).click(); // the line is still selected
  await top(page).getByRole('button', { name: 'Entered by mistake' }).click();
  await pin(page, '7402');
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await page.getByText('Removed. The kitchen gets a void slip.').waitFor();
  await page.locator('.line-void', { hasText: 'Cheesy chips' }).waitFor();
  await page.locator('.total-total', { hasText: '£10.39' }).waitFor(); // 7.99 + 2.40

  // Pay £5 by card, the rest in cash: open the till first.
  await page.getByRole('button', { name: 'Pay', exact: true }).click();
  await top(page).getByText('Open the till').waitFor();
  await pin(page, '5000');
  await top(page).getByRole('button', { name: 'Open till' }).click();
  await top(page).getByRole('button', { name: 'Card', exact: true }).click();
  await top(page).getByText('Key £10.39 into the card machine').waitFor();
  await pin(page, '500'); // typing replaces the pre-filled amount
  await top(page).getByLabel('Terminal receipt number').fill('004211');
  await top(page).getByRole('button', { name: 'Card approved £5.00' }).click();
  await page.getByText('£5.00 taken. £5.39 still to pay.').waitFor();
  await top(page).getByRole('button', { name: 'Cash', exact: true }).click();
  await shot(page, 'ui-part-paid');
  await top(page).getByRole('button', { name: '£10.00' }).click();
  await page.locator('.change-amount', { hasText: '£4.61' }).waitFor();
  await top(page).getByRole('button', { name: 'Done' }).click();
  await page.getByText('Paid. Receipt printing.').waitFor();

  // Collected from the orders list.
  await page.getByRole('button', { name: /^Orders/ }).click();
  await top(page).locator('.order-row', { hasText: number }).getByRole('button', { name: 'Collected' }).click();
  await page.getByText(`Order ${number} collected.`).waitFor();

  const order = (await ordersToday()).find(o => o.number === number);
  assert.equal(order.status, 'Completed');
  assert.equal(order.total.minorUnits, 1039);
  assert.equal(order.balance.minorUnits, 0);
  const payments = await call('GET', `/api/orders/${order.id}/payments`, { device: shop.owner.device, token: shop.owner.token });
  assert.deepEqual(payments.payments.map(p => [p.method, p.amount.minorUnits, p.reference ?? null]), [['Card', 500, '004211'], ['Cash', 539, null]]);
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('two tills see each other’s orders and menu changes live', async () => {
  const one = await tillPage(shop.till1);
  const two = await tillPage(shop.till2);
  await login(one, '5931');
  await login(two, '7402');
  await one.locator('.live-on').waitFor();
  await two.locator('.live-on').waitFor();
  const before = Number((await two.locator('.orders-count').textContent()) || '0');

  await pick(one, 'Drinks', 'Water 500ml');
  await one.getByRole('button', { name: 'Send', exact: true }).click();
  await two.locator('.orders-count', { hasText: String(before + 1) }).waitFor();

  // Sold out on the hub: both tills grey the button at once.
  const menu = await call('GET', '/api/menu', { device: shop.owner.device });
  const water = menu.products.find(p => p.name.en === 'Water 500ml');
  await call('POST', `/api/menu/products/${water.id}/sold-out`, shop.owner, { soldOut: true });
  await two.locator('.cats').getByRole('button', { name: 'Drinks', exact: true }).click();
  await two.locator('.item-out', { hasText: 'Water 500ml' }).waitFor();
  await one.locator('.item-out', { hasText: 'Water 500ml' }).waitFor();
  await call('POST', `/api/menu/products/${water.id}/sold-out`, shop.owner, { soldOut: false });
  await shot(two, 'ui-live');
  assert.deepEqual([...one.errors, ...two.errors], []);
  await one.context().close();
  await two.context().close();
});

test('phone and eat-in orders are saved with the right type; the till closes with a blind count', async () => {
  const page = await tillPage(shop.till1, { width: 1024, height: 768 });
  await login(page, '5931');
  await page.getByRole('button', { name: 'Phone', exact: true }).click();
  await pick(page, 'Starters', 'Garlic bread £');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.locator('.lines-empty').waitFor();
  await page.getByRole('button', { name: 'Eat in', exact: true }).click();
  await pick(page, 'Desserts', 'Cheesecake');
  await shot(page, 'ui-1024');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.locator('.lines-empty').waitFor(); // back to a new, empty order
  const orders = await ordersToday();
  assert.deepEqual(orders.slice(-2).map(o => [o.source, o.type]), [['Phone', 'Takeaway'], ['Counter', 'EatIn']]);
  // Cold cheesecake eaten in is standard-rated; garlic bread (hot) is too: both have VAT.
  assert.ok(orders.slice(-2).every(o => o.vat.length === 1 && o.vat[0].vat.minorUnits > 0));

  await page.getByRole('button', { name: 'More', exact: true }).click();
  await top(page).getByRole('button', { name: 'Close till (cash count)' }).click();
  await top(page).getByText('Count all the cash').waitFor();
  await pin(page, '6000');
  await top(page).getByRole('button', { name: 'Close till' }).click();
  await top(page).getByText('Till closed').waitFor();
  // Float £50 + £5.39 cash kept = £55.39 expected; £60 counted = £4.61 over.
  await top(page).locator('tr', { hasText: 'Cash expected in drawer' }).getByText('£55.39').waitFor();
  await top(page).locator('tr', { hasText: 'Over' }).getByText('£4.61').waitFor();
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('every printout was made: kitchen tickets, additions, the void slip and receipts', async () => {
  const jobs = await call('GET', '/api/print-jobs', { device: shop.owner.device, token: shop.owner.token });
  const kinds = new Set(jobs.map(j => j.job.kind));
  for (const kind of ['KitchenNew', 'KitchenAdditional', 'KitchenVoid', 'Receipt']) assert.ok(kinds.has(kind), kind);
  assert.ok(jobs.every(j => j.status === 'Printed'), 'all printed');
  assert.ok(jobs.every(j => fs.existsSync(j.output)), 'files exist');
});
