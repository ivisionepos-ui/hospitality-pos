// End-to-end tests of the till screen: a real hub, a real browser (Chromium via Playwright).
// Run from the repository root after a Release build:  node --test tests/ui/
// Needs Node 22+ and Playwright with Chromium (npm install in tests/ui, then npx playwright install chromium).
const { test, before, after, afterEach } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const http = require('node:http');
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

// A stand-in for postcodes.io (same answers format), so tests never need the internet.
// 1 mile north ≈ 0.01448° latitude. LS9 9XX answers "service down".
const PLACES = {
  'LS1 4AP': [53.8, -1.55, 'Leeds'],
  'LS2 9JT': [53.8 + 1 / 69.05, -1.55, 'Leeds'],
};
// Stand-ins for Google Places (New) and Royal Mail AddressNow (Loqate), in their documented answer formats.
const GOOGLE_KEY = 'google-good-key-1234';
const ROYAL_MAIL_KEY = 'AA11-BB22-CC33-DD44';
function fakeAddressService(req, res, url) {
  res.setHeader('Content-Type', 'application/json');
  const send = (status, body) => { res.statusCode = status; res.end(JSON.stringify(body)); };
  const north = 53.8 + 1 / 69.05;
  if (url.pathname.startsWith('/v1/places')) {
    if (req.headers['x-goog-api-key'] !== GOOGLE_KEY) return send(403, { error: { code: 403, message: 'API key not valid. Please pass a valid API key.' } });
    if (url.pathname === '/v1/places:autocomplete') {
      return send(200, { suggestions: [{ placePrediction: { placeId: 'place-shop', text: { text: 'Corner Shop, 7 Test Road, Leeds LS2 9JT, UK' } } }] });
    }
    const c = (longText, ...types) => ({ longText, shortText: longText, types });
    return send(200, {
      types: ['store', 'establishment'], displayName: { text: 'Corner Shop' }, location: { latitude: north, longitude: -1.55 },
      addressComponents: [c('7', 'street_number'), c('Test Road', 'route'), c('Leeds', 'postal_town'), c('LS2 9JT', 'postal_code')],
    });
  }
  if (url.searchParams.get('Key') !== ROYAL_MAIL_KEY) return send(200, { Items: [{ Error: '2', Description: 'Unknown key', Cause: 'Key not found.', Resolution: 'Please check that the key is correct.' }] });
  if (url.pathname.includes('/Find/')) {
    return url.searchParams.get('Container')
      ? send(200, { Items: [{ Id: 'GB|RM|A|9', Type: 'Address', Text: '9 Test Road', Highlight: '', Description: 'Leeds, LS2 9JT' }] })
      : send(200, { Items: [{ Id: 'GB|RM|ENG|9JT-LS2', Type: 'Postcode', Text: 'LS2 9JT', Highlight: '0-7', Description: 'Test Road, Leeds - 1 Address' }] });
  }
  return send(200, { Items: [{ BuildingNumber: '9', Street: 'Test Road', City: 'Leeds', PostalCode: 'LS2 9JT', Field1: String(north), Field2: '-1.55' }] });
}
let postcodeServer;
let postcodeBase;

function startPostcodeService() {
  postcodeServer = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname.startsWith('/v1/places') || url.pathname.startsWith('/Capture/')) { fakeAddressService(req, res, url); return; }
    const code = decodeURIComponent(req.url.split('/postcodes/')[1] ?? '').toUpperCase();
    res.setHeader('Content-Type', 'application/json');
    if (code === 'LS9 9XX') { res.statusCode = 500; res.end('{"status":500}'); return; }
    const place = PLACES[code];
    if (!place) { res.statusCode = 404; res.end('{"status":404,"error":"Postcode not found"}'); return; }
    res.end(JSON.stringify({ status: 200, result: { postcode: code, latitude: place[0], longitude: place[1], admin_district: place[2] } }));
  });
  return new Promise(resolve => postcodeServer.listen(0, '127.0.0.1', () => {
    postcodeBase = `http://127.0.0.1:${postcodeServer.address().port}`;
    resolve();
  }));
}

async function startHub() {
  await startPostcodeService();
  data = fs.mkdtempSync(path.join(os.tmpdir(), 'pos-ui-'));
  const port = 5200 + Math.floor(Math.random() * 700);
  base = `http://localhost:${port}`;
  hub = spawn(dotnet, [dll,
    `--Hub:LocalHttpPort=${port}`, '--Hub:NetworkHttpsPort=0',
    `--Hub:DatabasePath=${path.join(data, 'data', 'hub.db')}`,
    `--Backup:Folder=${path.join(data, 'data', 'backups')}`,
    `--Printing:Receipt:Folder=${path.join(data, 'prints', 'receipts')}`,
    `--Printing:Kitchen:Folder=${path.join(data, 'prints', 'kitchen')}`,
    '--Delivery:ShopPostcode=LS1 4AP', `--Delivery:PostcodeService=${postcodeBase}`,
    `--Delivery:GoogleEndpoint=${postcodeBase}`, `--Delivery:RoyalMailEndpoint=${postcodeBase}`,
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
  const context = await browser.newContext({ viewport, acceptDownloads: true });
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
  postcodeServer?.close();
  fs.rmSync(data, { recursive: true, force: true });
});

test('first-time set-up screen appears only on a fresh hub, on the hub PC', async () => {
  // This hub is set up, so a new browser is asked to register with a code instead.
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(`${base}/pos/`);
  await page.getByText('Register this device').waitFor();
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
  await page.getByRole('button', { name: 'Collection', exact: true }).click();
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

test('manager edits the menu on the order layout; the cashier till updates live; clashes are caught', async () => {
  const owner = { device: shop.owner.device, token: shop.owner.token };
  const product = async name => (await call('GET', '/api/menu', owner)).products.find(p => p.name.en === name);
  const cashier = await tillPage(shop.till1);
  await login(cashier, '5931');
  const page = await tillPage(shop.till2);
  await login(page, '7402');

  // Cashiers have no Edit menu, only Sold out items.
  await cashier.getByRole('button', { name: 'More', exact: true }).click();
  assert.equal(await top(cashier).getByRole('button', { name: 'Edit menu' }).count(), 0);
  await top(cashier).getByRole('button', { name: 'Sold out items' }).click();
  await top(cashier).getByRole('button', { name: 'Drinks', exact: true }).click();
  await top(cashier).getByRole('button', { name: /Bottle 1.5L/ }).click();
  await top(cashier).getByText('SOLD OUT — tap to bring back').waitFor();
  await top(cashier).getByRole('button', { name: /Bottle 1.5L/ }).click();
  await top(cashier).getByRole('button', { name: 'Done' }).click();
  assert.equal((await product('Bottle 1.5L')).isSoldOut, false);

  await page.getByRole('button', { name: 'More', exact: true }).click();
  await top(page).getByRole('button', { name: 'Edit menu' }).click();
  await page.getByText('EDIT MENU', { exact: true }).waitFor();

  // Rename and re-price the 10" Margherita.
  await page.locator('.cats').getByRole('button', { name: 'Pizzas' }).click();
  await page.locator('.items').getByRole('button', { name: /Margherita/ }).click();
  await page.getByLabel('Name (on receipts and kitchen tickets)').fill('Margherita classic');
  await page.getByRole('button', { name: /Price of 10"/ }).click();
  await pin(page, '850');
  await top(page).getByRole('button', { name: 'Set price' }).click();
  await shot(page, 'ui-edit-item');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByText('Margherita classic saved. All tills updated.').waitFor();
  const margherita = await product('Margherita classic');
  assert.deepEqual(margherita.variants.map(v => v.price.minorUnits), [850, 999, 1199]);
  await cashier.locator('.cats').getByRole('button', { name: 'Pizzas', exact: true }).click();
  await cashier.locator('.items').getByRole('button', { name: /Margherita classic.*from £8\.50/ }).waitFor(); // live on the other till

  // A second size for coleslaw; sauce offered with the cheeseburger; a new sauce choice.
  await page.locator('.cats').getByRole('button', { name: 'Sides' }).click();
  await page.locator('.items').getByRole('button', { name: /Coleslaw/ }).click();
  await page.getByRole('button', { name: '+ Add size' }).click();
  await page.getByRole('button', { name: /Price of Large/ }).click();
  await pin(page, '250');
  await top(page).getByRole('button', { name: 'Set price' }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByText('Coleslaw saved.').waitFor();
  assert.deepEqual((await product('Coleslaw')).variants.map(v => [v.name.en, v.price.minorUnits]), [['Regular', 150], ['Large', 250]]);

  await page.locator('.cats').getByRole('button', { name: 'Burgers' }).click();
  await page.locator('.items').getByRole('button', { name: /^Cheeseburger/ }).click();
  await page.getByRole('button', { name: 'Sauce', exact: true }).click();
  await page.getByRole('button', { name: 'Edit Sauce' }).click();
  await top(page).getByRole('button', { name: '+ Add choice' }).click();
  await top(page).getByLabel('Choice 6 name').fill('Harissa');
  await top(page).getByRole('button', { name: 'Price of Harissa' }).click();
  await pin(page, '30');
  await top(page).getByRole('button', { name: 'Set price' }).click();
  await top(page).getByRole('button', { name: 'Save extras' }).click();
  await page.getByText('Sauce saved. Every item using it is updated.').waitFor();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByText('Cheeseburger saved.').waitFor();
  const menu = await call('GET', '/api/menu', owner);
  const sauce = menu.modifierGroups.find(g => g.name.en === 'Sauce');
  assert.ok((await product('Cheeseburger')).modifierGroupIds.includes(sauce.id));
  assert.deepEqual(sauce.options.at(-1).name.en + ' ' + sauce.options.at(-1).price.minorUnits, 'Harissa 30');

  // A new category with a new item.
  await page.getByRole('button', { name: '+ Category' }).click();
  await top(page).getByLabel('Name, e.g. Wraps').fill('Wraps');
  await top(page).getByRole('button', { name: 'Add' }).click();
  await page.getByText('Wraps added.').waitFor();
  await page.getByRole('button', { name: '+ Add item' }).click();
  await page.getByLabel('Name (on receipts and kitchen tickets)').fill('Falafel wrap');
  await page.getByRole('button', { name: /Price of Regular/ }).click();
  await pin(page, '599');
  await top(page).getByRole('button', { name: 'Set price' }).click();
  await page.getByRole('button', { name: 'Add item', exact: true }).click();
  await page.getByText('Falafel wrap saved.').waitFor();
  const wraps = (await call('GET', '/api/menu', owner)).categories.find(c => c.name.en === 'Wraps');
  assert.equal((await product('Falafel wrap')).categoryId, wraps.id);
  await cashier.locator('.cats').getByRole('button', { name: 'Wraps', exact: true }).waitFor();

  // Move Pepperoni to the front, then undo it.
  await page.locator('.cats').getByRole('button', { name: 'Pizzas' }).click();
  await page.locator('.items').getByRole('button', { name: /Pepperoni/ }).click();
  await page.getByRole('button', { name: '◀ Earlier' }).click();
  await page.locator('.items .item').first().filter({ hasText: 'Pepperoni' }).waitFor();
  await page.getByRole('button', { name: 'Undo last' }).click();
  await page.getByText(/^Undid: Moved "Pepperoni"/).waitFor();
  await page.locator('.items .item').first().filter({ hasText: 'Margherita' }).waitFor();

  // Someone else changes Hawaiian while it is open here: the save is refused, nothing is lost.
  await page.locator('.items').getByRole('button', { name: /Hawaiian/ }).click();
  const hawaiian = await product('Hawaiian');
  await call('POST', `/api/menu/products/${hawaiian.id}/price`, owner, { variantId: hawaiian.variants[0].id, pricePence: 950 });
  await page.getByLabel('Name (on receipts and kitchen tickets)').fill('Hawaiian special');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByText(/Someone else changed this on another till/).waitFor();
  const after = await product('Hawaiian');
  assert.equal(after.variants[0].price.minorUnits, 950);

  await page.getByRole('button', { name: 'Cancel' }).click();
  await top(page).getByRole('button', { name: 'Leave' }).click();
  await page.getByRole('button', { name: 'Done' }).click();
  await page.locator('.ticket-head').waitFor();
  assert.deepEqual([...page.errors, ...cashier.errors], []);
  await page.context().close();
  await cashier.context().close();
});

test('phone delivery: postcode check, minimum order, remembered customer, manager-approved fee, no-internet miles', async () => {
  const owner = { device: shop.owner.device, token: shop.owner.token };
  const page = await tillPage(shop.till1);
  await login(page, '5931');

  // Delivery opens the customer panel straight away.
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Phone number').fill('07700 900456');
  await top(page).getByLabel('Name').fill('Priya Shah');
  await top(page).getByLabel('House number and street').fill('12 Park Lane');
  await top(page).getByLabel('Postcode').fill('ls29jt');
  await top(page).getByLabel('Notes for the driver (optional)').fill('Ring the side door');
  await top(page).getByRole('button', { name: 'Check postcode' }).click();
  await top(page).getByText('1.3 miles from the shop · Leeds').waitFor();
  assert.equal(await top(page).getByLabel('Postcode').inputValue(), 'LS2 9JT');
  await shot(page, 'ui-delivery-customer');
  await top(page).getByRole('button', { name: 'Use these details' }).click();
  await page.locator('.customer-btn', { hasText: 'Priya Shah · 07700 900456 · 12 Park Lane, LS2 9JT' }).waitFor();

  // Below the £12 minimum: Send and Pay are blocked, with the reason.
  await pick(page, 'Pizzas', 'Margherita');
  await top(page).getByRole('button', { name: /^10"/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  // (The menu-editing test above changed the 10" Margherita to £8.50.)
  await page.locator('.ticket-warning', { hasText: 'Minimum order for delivery is £12.00: add £3.50 more.' }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Send', exact: true }).isDisabled(), true);
  await pick(page, 'Sides', 'Coleslaw'); // has two sizes since the menu-editing test
  await top(page).getByRole('button', { name: /^Regular/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await page.locator('.total-row', { hasText: 'Delivery (1.3 mi)' }).getByText('£1.50').waitFor();
  await page.locator('.total-total', { hasText: '£11.50' }).waitFor(); // 8.50 + 1.50 coleslaw + 1.50 delivery: food £10.00 still under £12
  await page.locator('.ticket-warning').waitFor();
  await pick(page, 'Drinks', 'Bottle 1.5L');
  await page.locator('.ticket-warning').waitFor({ state: 'detached' });
  await shot(page, 'ui-delivery-order');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await page.locator('.lines-empty').waitFor();
  let order = (await call('GET', '/api/orders', owner)).at(-1);
  assert.equal(order.type, 'Delivery');
  assert.equal(order.contact.notes, 'Ring the side door');
  assert.equal(order.delivery.fee.minorUnits, 150);
  assert.equal(order.total.minorUnits, 850 + 150 + 299 + 150);

  // Next call from the same number: typing part of it finds Priya and fills everything in.
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Phone number').fill('900456');
  await top(page).getByRole('button', { name: /Priya Shah/ }).click();
  await top(page).getByText('1.3 miles from the shop').waitFor();
  assert.equal(await top(page).getByLabel('House number and street').inputValue(), '12 Park Lane');
  await top(page).getByRole('button', { name: 'Use these details' }).click();

  // Free delivery agreed by a manager.
  await page.getByRole('button', { name: 'Change delivery fee' }).click();
  await pin(page, '0');
  await top(page).getByRole('button', { name: 'Next' }).click();
  await top(page).getByRole('button', { name: 'Regular customer' }).click();
  await pick(page, 'Pizzas', 'Pepperoni');
  await top(page).getByRole('button', { name: /^14"/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await page.locator('.total-row', { hasText: 'changed' }).waitFor();
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await top(page).getByText('Manager, enter your PIN.').waitFor();
  await pin(page, '7402');
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await page.locator('.lines-empty').waitFor();
  order = (await call('GET', '/api/orders', owner)).at(-1);
  assert.equal(order.delivery.overridden, true);
  assert.equal(order.total.minorUnits, 1299);
  const priya = await call('GET', '/api/customers?phone=07700900456', owner);
  assert.equal(priya[0].orderCount, 2);

  // No internet: the postcode cannot be checked, so the miles are typed in. Too far is blocked.
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Phone number').fill('0113 496 0000');
  await top(page).getByLabel('House number and street').fill('1 Mill Road');
  await top(page).getByLabel('Postcode').fill('LS9 9XX');
  await top(page).getByRole('button', { name: 'Check postcode' }).click();
  await top(page).getByText(/Type the distance in miles instead/).waitFor();
  await top(page).getByLabel('Distance in miles').fill('9'); // the miles box opens by itself
  await top(page).getByRole('button', { name: 'Use these details' }).click();
  await pick(page, 'Pizzas', 'Meat feast');
  await top(page).getByRole('button', { name: /^14"/ }).click();
  await top(page).getByRole('button', { name: /^Add/ }).click();
  await page.locator('.ticket-warning', { hasText: 'Too far to deliver (9 miles)' }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Send', exact: true }).isDisabled(), true);
  await page.locator('.customer-btn').click();
  await top(page).getByText('No internet? Type the miles').click();
  await top(page).getByLabel('Distance in miles').fill('3.5');
  await top(page).getByRole('button', { name: 'Use these details' }).click();
  await page.locator('.total-row', { hasText: 'Delivery (3.5 mi)' }).getByText('£3.00').waitFor();
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('owner adds a Royal Mail address file; the cashier picks the flat from the postcode', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await top(owner).getByRole('button', { name: 'Address files' }).click();
  await top(owner).getByText('No address files yet.').waitFor();
  // Made-up addresses in the same 14-column layout as Royal Mail's files.
  const csv = [
    ',,,,,10,,Test Road,,,LEEDS,LS2 9JT,S,1A',
    ',,,,,2,,Test Road,,,LEEDS,LS2 9JT,S,1B',
    ',,,Example Tower,Flat 10,,,Test Road,,,LEEDS,LS2 9JT,S,1C',
    ',,,Example Tower,Flat 2,,,Test Road,,,LEEDS,LS2 9JT,S,1D',
    'Sample Cafe,,,25-27,,,,Test Road,,,LEEDS,LS2 9JT,S,1E',
  ].join('\n');
  await top(owner).getByLabel('Choose address files').setInputFiles({ name: 'LS2.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await owner.getByText('LS2.csv: 5 addresses loaded.').waitFor();
  await top(owner).locator('tr', { hasText: 'LS2.csv' }).getByText(/5 addresses · 1 postcodes/).waitFor();
  await top(owner).getByRole('button', { name: 'Done' }).click();
  await owner.context().close();

  const page = await tillPage(shop.till1);
  await login(page, '5931');
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Phone number').fill('07700 900789');
  await top(page).getByLabel('Find address').fill('ls29jt');
  await top(page).locator('.pick-item').first().waitFor();
  assert.deepEqual(await top(page).locator('.pick-item').allTextContents(),
    ['2 Test Road', '10 Test Road', 'Sample Cafe, 25-27 Test Road', 'Flat 2, Example Tower, Test Road', 'Flat 10, Example Tower, Test Road']);
  await top(page).getByLabel('Find address').fill('ls2 9jt flat 10');
  await top(page).locator('.pick-item', { hasText: 'Flat 10' }).waitFor();
  await shot(page, 'ui-address-pick');
  await top(page).getByLabel('Find address').press('Enter'); // one match: Enter picks it
  await top(page).getByText('✓ Flat 10, Example Tower, Test Road — change').waitFor();
  assert.equal(await top(page).getByLabel('House number and street').inputValue(), 'Test Road');
  assert.equal(await top(page).getByLabel('Flat or building (optional)').inputValue(), 'Flat 10, Example Tower');
  assert.equal(await top(page).getByLabel('Town (optional)').inputValue(), 'Leeds');
  await top(page).getByText('1.3 miles from the shop').waitFor(); // distance worked out by itself
  await top(page).getByRole('button', { name: 'Use these details' }).click();
  await page.locator('.customer-btn', { hasText: 'Test Road, LS2 9JT' }).waitFor();
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('owner chooses Google, then Royal Mail online, testing each key; the till finds addresses with each, and falls back to the file', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  const open = async () => {
    await owner.getByRole('button', { name: 'More', exact: true }).click();
    await top(owner).getByRole('button', { name: 'Address lookup' }).click();
  };
  await open();
  assert.deepEqual(await top(owner).locator('.provider-title').allTextContents(), ['Google', 'Royal Mail online', 'Local Royal Mail file']);
  assert.equal(await top(owner).getByRole('radio', { name: /Local Royal Mail file/ }).getAttribute('aria-checked'), 'true');

  // Google with a wrong key: Test explains; then the right key.
  await top(owner).getByRole('radio', { name: /Google/ }).click();
  await top(owner).getByLabel('Google API key').fill('wrong-key');
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Find address now uses: Google.').waitFor();
  await top(owner).getByRole('button', { name: 'Test Google' }).click();
  await top(owner).getByText(/Google: Google refused the key \(403\)/).waitFor();
  await top(owner).getByLabel('Google API key').fill(GOOGLE_KEY);
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await top(owner).getByText('Key saved').first().waitFor();
  await top(owner).getByRole('button', { name: 'Test Google' }).click();
  await top(owner).getByText(/Google works: 1 found, e.g. Corner Shop/).waitFor();
  await shot(owner, 'ui-address-lookup');
  await top(owner).getByRole('button', { name: 'Cancel' }).click();

  const page = await tillPage(shop.till1);
  await login(page, '5931');
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Phone number').fill('07700 900321');
  await top(page).getByLabel('Find address').fill('corner shop test road');
  await top(page).getByText('Results from Google').waitFor();
  await top(page).locator('.pick-item', { hasText: 'Corner Shop' }).click();
  await top(page).getByText('1.3 miles from the shop').waitFor();
  assert.equal(await top(page).getByLabel('House number and street').inputValue(), '7 Test Road');
  assert.equal(await top(page).getByLabel('Flat or building (optional)').inputValue(), 'Corner Shop');
  await top(page).getByRole('button', { name: 'Cancel' }).click();

  // Royal Mail online: a postcode opens into its addresses.
  await open();
  await top(owner).getByRole('radio', { name: /Royal Mail online/ }).click();
  await top(owner).getByLabel('Royal Mail AddressNow key').fill(ROYAL_MAIL_KEY);
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Find address now uses: Royal Mail online.').waitFor();
  await top(owner).getByRole('button', { name: 'Cancel' }).click();
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Find address').fill('LS2 9JT');
  await top(page).locator('.pick-group', { hasText: 'LS2 9JT, Test Road, Leeds - 1 Address ›' }).click();
  await top(page).locator('.pick-item', { hasText: '9 Test Road, Leeds, LS2 9JT' }).click();
  await top(page).getByText('1.3 miles from the shop').waitFor();
  assert.equal(await top(page).getByLabel('House number and street').inputValue(), '9 Test Road');
  await top(page).getByRole('button', { name: 'Cancel' }).click();

  // A broken key: the till carries on with the local file, and says why.
  await open();
  await top(owner).getByLabel('Royal Mail AddressNow key').fill('expired-key');
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await top(owner).getByRole('button', { name: 'Cancel' }).click();
  await page.getByRole('button', { name: 'Delivery', exact: true }).click();
  await top(page).getByLabel('Find address').fill('LS2 9JT flat 2');
  await top(page).getByText(/Showing the shop's own address file instead/).waitFor();
  await top(page).locator('.pick-item', { hasText: 'Flat 2, Example Tower, Test Road' }).waitFor();
  await top(page).getByRole('button', { name: 'Cancel' }).click();

  // Back to the local file for the tests that follow.
  await open();
  await top(owner).getByRole('radio', { name: /Local Royal Mail file/ }).click();
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Find address now uses: Local Royal Mail file.').waitFor();
  assert.deepEqual([...page.errors, ...owner.errors], []);
  await page.context().close();
  await owner.context().close();
});

test('owner enters the shop name and address in Shop details; mistakes are explained', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await top(owner).getByRole('button', { name: 'Shop details' }).click();
  await top(owner).getByLabel('Shop name (top of the receipt)').fill('Sample Cafe');
  // Find the shop's address from the postcode (the local file loaded earlier has it).
  await top(owner).getByLabel('Find address').fill('ls29jt cafe');
  await top(owner).getByLabel('Find address').press('Enter');
  await top(owner).getByText('✓ LS2 9JT found (Leeds). Delivery distances will be measured from here.').waitFor();
  assert.equal(await top(owner).getByLabel('Address line 1').inputValue(), '25-27 Test Road');
  assert.equal(await top(owner).getByLabel('Address line 2 (optional)').inputValue(), ''); // the shop's own name is not repeated
  assert.equal(await top(owner).getByLabel('Town').inputValue(), 'Leeds');
  // A typed postcode is checked too.
  await top(owner).getByLabel('Postcode (delivery distances are measured from here)').fill('ls14ap');
  await top(owner).getByRole('button', { name: 'Check postcode' }).click();
  await top(owner).getByText('✓ LS1 4AP found (Leeds).', { exact: false }).waitFor();
  await top(owner).getByLabel('Phone number').fill('0113 4960000');
  await top(owner).getByLabel('VAT number (if VAT registered)').fill('12345');
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await top(owner).getByText('"12345" is not a UK VAT number (9 digits, e.g. GB 123 4567 89).').waitFor();
  await top(owner).getByLabel('VAT number (if VAT registered)').fill('123456789');
  await shot(owner, 'ui-shop-details');
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Shop details saved for Sample Cafe. New receipts use them.').waitFor();
  const saved = await call('GET', '/api/settings/shop', { device: shop.owner.device, token: shop.owner.token });
  assert.deepEqual([saved.businessName, saved.addressLine1, saved.postcode, saved.phone, saved.vatNumber], ['Sample Cafe', '25-27 Test Road', 'LS1 4AP', '01134 960000', 'GB 123 4567 89']);
  assert.deepEqual(owner.errors, []);
  await owner.context().close();
});

test('owner opens Add a till or screen: the code and the steps for another PC are shown', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await top(owner).getByRole('button', { name: 'Add a till or screen' }).click();
  await top(owner).getByText('Add a till or kitchen screen').waitFor();
  assert.match(await top(owner).locator('.pair-code').innerText(), /^\d{6}$/);
  await top(owner).getByText('Connect this PC to the hub', { exact: false }).waitFor();
  await top(owner).getByText(/Certificate check code: [0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}/).waitFor();
  await shot(owner, 'ui-add-till');
  await top(owner).getByRole('button', { name: 'Done' }).click();
  assert.deepEqual(owner.errors, []);
  await owner.context().close();
});

async function pinIn(page, digits) {
  await pin(page, digits);
  await top(page).locator('.keypad-enter').click();
}

test('owner adds, edits, removes and brings back staff; the new person changes their own PIN; a manager sees less', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await top(owner).getByRole('button', { name: 'Staff', exact: true }).click();
  await top(owner).getByLabel('Olivia Owner').getByText('Olivia Owner (you)').waitFor();

  // Add: name and role, then the PIN twice. An easy PIN is explained before anything is saved.
  await top(owner).getByRole('button', { name: '+ Add staff' }).click();
  await top(owner).getByLabel('Name').fill('Wendy  Waiter');
  await top(owner).getByRole('radio', { name: 'Waiter' }).click();
  await top(owner).getByRole('button', { name: 'Next: PIN' }).click();
  await pinIn(owner, '2222');
  await top(owner).getByText('That PIN is too easy to guess (like 1111 or 1234). Choose another.').waitFor();
  await pinIn(owner, '6152');
  await pinIn(owner, '6159');
  await top(owner).getByText('The two PINs are different. Start again.').waitFor();
  await pinIn(owner, '6152');
  await pinIn(owner, '6152');
  await owner.getByText('Wendy Waiter added as Waiter. They log in with their PIN.').waitFor();

  // A second person with the same name is refused by the hub, and the form comes back to fix it.
  await top(owner).getByRole('button', { name: '+ Add staff' }).click();
  await top(owner).getByLabel('Name').fill('wendy waiter');
  await top(owner).getByRole('button', { name: 'Next: PIN' }).click();
  await pinIn(owner, '6170');
  await pinIn(owner, '6170');
  await owner.getByText('Someone called wendy waiter is already on the staff list. Add a surname or initial.').waitFor();
  await top(owner).getByRole('button', { name: 'Close' }).click(); // the PIN asked again: give up

  // Edit: make Wendy a cashier.
  const wendy = () => top(owner).getByLabel('Wendy Waiter', { exact: true });
  await wendy().getByRole('button', { name: 'Edit' }).click();
  await top(owner).getByRole('radio', { name: 'Cashier' }).click();
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Wendy Waiter is now Cashier. They need to log in again.').waitFor();
  await wendy().getByText('Cashier', { exact: true }).waitFor();

  // Remove, then bring back with a new PIN.
  await wendy().getByRole('button', { name: 'Remove' }).click();
  await top(owner).getByRole('button', { name: 'Remove' }).click();
  await wendy().getByText('Cashier · removed').waitFor();
  await wendy().getByRole('button', { name: 'Bring back' }).click();
  await pinIn(owner, '6153');
  await pinIn(owner, '6153');
  await owner.getByText('Wendy Waiter is back as Cashier.').waitFor();
  await shot(owner, 'ui-staff');

  await top(owner).getByRole('button', { name: 'All changes' }).click();
  for (const change of ['Added as Waiter', 'Role Waiter → Cashier', 'Removed', 'Brought back']) await top(owner).getByRole('cell', { name: change, exact: true }).waitFor();
  assert.equal(await top(owner).getByText('6152').count(), 0); // PINs are never shown
  await shot(owner, 'ui-staff-history');
  await top(owner).getByRole('button', { name: 'Done' }).click();
  await top(owner).getByRole('button', { name: 'Done' }).click();

  // Tills and screens: the owner's own till cannot be removed from itself.
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await shot(owner, 'ui-more');
  await top(owner).getByRole('button', { name: 'Tills and screens' }).click();
  await top(owner).getByText('Till 2 (this one)').waitFor();
  assert.equal(await top(owner).getByLabel('Till 2', { exact: true }).getByRole('button', { name: 'Remove' }).count(), 0);
  await top(owner).getByRole('button', { name: 'Done' }).click();

  // Wendy logs in and changes her own PIN; she must log in again with it.
  const page = await tillPage(shop.till1);
  await login(page, '6153');
  await page.getByRole('button', { name: 'More', exact: true }).click();
  assert.equal(await top(page).getByRole('button', { name: 'Staff', exact: true }).count(), 0); // cashiers do not manage staff
  await top(page).getByRole('button', { name: 'Change my PIN' }).click();
  await pinIn(page, '6158'); // wrong current PIN
  await pinIn(page, '6154');
  await pinIn(page, '6154');
  await page.getByText('Your current PIN is wrong.').waitFor();
  await page.getByRole('button', { name: 'More', exact: true }).click();
  await top(page).getByRole('button', { name: 'Change my PIN' }).click();
  await pinIn(page, '6153');
  await pinIn(page, '6154');
  await pinIn(page, '6154');
  await page.getByText('PIN changed. Log in with your new PIN.').waitFor();
  await login(page, '6154');

  // A manager sees the staff list but cannot change the owner or make managers.
  await page.getByRole('button', { name: 'Log out' }).click();
  await login(page, '7402');
  await page.getByRole('button', { name: 'More', exact: true }).click();
  await top(page).getByRole('button', { name: 'Staff', exact: true }).click();
  assert.equal(await top(page).getByLabel('Olivia Owner', { exact: true }).getByRole('button', { name: 'Edit' }).count(), 0);
  await top(page).getByLabel('Wendy Waiter', { exact: true }).getByRole('button', { name: 'Edit' }).click();
  assert.deepEqual(await top(page).getByRole('radio').allInnerTexts().then(t => t.map(x => x.split('\n')[0])), ['Cashier', 'Waiter', 'Kitchen', 'Driver']);
  await top(page).getByRole('button', { name: 'Cancel' }).click();

  assert.deepEqual([...page.errors, ...owner.errors], []);
  await page.context().close();
  await owner.context().close();
});

test('owner sets discount limits; cashier gives a discount (manager above the limit), pays, then refunds part to the card', async () => {
  const owner = await tillPage(shop.till2);
  await login(owner, '4826');
  await owner.getByRole('button', { name: 'More', exact: true }).click();
  await top(owner).getByRole('button', { name: 'Discount limits' }).click();
  await top(owner).getByLabel('Largest % off without a manager').fill('10');
  await top(owner).getByLabel('Largest £ off without a manager').fill('5');
  await top(owner).getByRole('button', { name: 'Save' }).click();
  await owner.getByText('Saved. Without a manager: up to 10% and £5.00 off.').waitFor();
  await owner.context().close();

  const page = await tillPage(shop.till1);
  await login(page, '5931');
  for (let i = 0; i < 5; i++) await pick(page, 'Drinks', 'Can 330ml');
  await page.locator('.total-total', { hasText: '£6.00' }).waitFor();

  // 20% is over the cashier's 10%: the order is saved (not sent yet) and a manager approves.
  await page.getByRole('button', { name: 'Discount', exact: true }).click();
  await top(page).getByText('Without a manager: up to 10% and £5.00 off.').waitFor();
  await top(page).getByRole('button', { name: '20%' }).click();
  await top(page).getByText('20% off: −£1.20, items now £4.80').waitFor();
  await top(page).getByRole('button', { name: 'Apply discount' }).click();
  await top(page).getByText('Choose or type a reason.').waitFor();
  await top(page).getByRole('button', { name: 'Complaint' }).click();
  await shot(page, 'ui-discount');
  await top(page).getByRole('button', { name: 'Apply discount' }).click();
  await top(page).getByText('Staff need a manager for discounts over 10% or £5.00. Manager, enter your PIN.').waitFor();
  await pin(page, '7402');
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await page.getByText('Discount −£1.20 (Complaint).').waitFor();
  await page.locator('.total-row', { hasText: 'Discount (Complaint)' }).waitFor();
  await page.locator('.total-total', { hasText: '£4.80' }).waitFor();
  await page.getByRole('button', { name: 'Send', exact: true }).waitFor(); // saved, not in the kitchen yet
  const order = (await ordersToday()).at(-1);
  assert.equal(order.lines.every(l => !l.sentToKitchen), true);

  // Change it to 10%: within the limit, no manager.
  await page.getByRole('button', { name: 'Change discount' }).click();
  await top(page).getByText('Now: −£1.20 (Complaint). A new discount replaces it.').waitFor();
  await top(page).getByRole('button', { name: '10%' }).click();
  await top(page).getByRole('button', { name: 'Regular customer' }).click();
  await top(page).getByRole('button', { name: 'Apply discount' }).click();
  await page.getByText('Discount −£0.60 (Regular customer).').waitFor();
  await page.locator('.total-total', { hasText: '£5.40' }).waitFor();

  // Pay by card (opening the till if it is closed); paying sends it to the kitchen.
  await page.getByRole('button', { name: 'Pay', exact: true }).click();
  if (await top(page).getByText('Open the till').isVisible().catch(() => false) || await top(page).getByText('Open the till').waitFor({ timeout: 1500 }).then(() => true, () => false)) {
    await pin(page, '5000');
    await top(page).getByRole('button', { name: 'Open till' }).click();
  }
  await top(page).getByRole('button', { name: 'Card', exact: true }).click();
  await top(page).getByLabel('Terminal receipt number').fill('007777');
  await top(page).getByRole('button', { name: 'Card approved £5.40' }).click();
  await page.getByText('Paid. Receipt printing.').waitFor();

  // Refund £2.00 of the card payment: reason, manager PIN, then reminded to do it on the card machine.
  await page.getByRole('button', { name: /^Orders/ }).click();
  await top(page).getByRole('button', { name: new RegExp(`^${order.number}`) }).click();
  await page.getByRole('button', { name: 'More for this order' }).click();
  assert.equal(await top(page).getByRole('button', { name: 'Discount' }).count(), 1); // still open (not collected), so a discount is possible
  await top(page).getByRole('button', { name: 'Refund' }).click();
  await top(page).getByLabel('Card £5.40').getByRole('button', { name: 'Refund up to £5.40' }).click();
  await pin(page, '200');
  await top(page).getByRole('button', { name: 'Next' }).click();
  await top(page).getByRole('button', { name: 'Wrong order' }).click();
  await top(page).getByText('Refunds need a manager. Manager, enter your PIN.').waitFor();
  await pin(page, '7402');
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await top(page).getByText('Now refund £2.00 on the card machine').waitFor();
  await top(page).getByRole('button', { name: 'OK' }).click();
  await top(page).getByRole('button', { name: 'Print' }).click();
  await page.getByText('Refund recorded.').waitFor();
  await page.locator('.total-row', { hasText: 'Paid' }).getByText('£3.40').waitFor();
  await page.locator('.total-row', { hasText: 'Refunded' }).getByText('-£2.00').waitFor();
  assert.equal(await page.locator('.total-due').count(), 0); // money back for a complaint is not owed again
  await page.getByRole('button', { name: 'Collected' }).waitFor();

  // The rest can still be refunded; the payment shows what was already given back.
  await page.getByRole('button', { name: 'More for this order' }).click();
  await top(page).getByRole('button', { name: 'Refund' }).click();
  await top(page).getByText('£2.00 already refunded', { exact: false }).waitFor();
  await top(page).getByRole('button', { name: 'Refund up to £3.40' }).waitFor();
  await shot(page, 'ui-refund');
  await top(page).getByRole('button', { name: 'Close' }).click();

  // A discount now would take the total below what was paid (£3.40 paid, 50% → £3.00): refused, explained.
  await page.getByRole('button', { name: 'Change discount' }).click();
  await top(page).getByRole('button', { name: '50%' }).click();
  await top(page).getByRole('button', { name: 'Regular customer' }).click();
  await top(page).getByRole('button', { name: 'Apply discount' }).click();
  await pin(page, '7402');
  await top(page).getByRole('button', { name: 'Approve' }).click();
  await page.getByText('£3.40 has already been paid, more than the new total. Give the money back as a refund instead.').waitFor();

  // Earlier days can be looked up (for a refund on yesterday's order).
  await page.getByRole('button', { name: /^Orders/ }).click();
  await top(page).getByRole('button', { name: 'Previous day' }).click();
  await top(page).getByText('Yesterday').waitFor();
  await top(page).getByText('No orders that day.').waitFor();
  await top(page).getByRole('button', { name: 'Next day' }).click();
  await top(page).getByText('Today').waitFor();
  await top(page).getByRole('button', { name: 'Close' }).click();

  await page.getByRole('button', { name: 'Collected' }).click();
  await page.getByText(`Order ${order.number} collected.`).waitFor();

  const saved = (await ordersToday()).find(o => o.id === order.id);
  assert.equal(saved.status, 'Completed');
  assert.equal(saved.total.minorUnits, 540);
  assert.equal(saved.discountReason, 'Regular customer');
  const payments = await call('GET', `/api/orders/${order.id}/payments`, { device: shop.owner.device, token: shop.owner.token });
  assert.deepEqual(payments.payments.map(p => [p.method, p.amount.minorUnits, p.reason ?? null]), [['Card', 540, null], ['Card', -200, 'Wrong order']]);
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

/** If the till's shift is closed, the "Open the till" question comes up first: open it with a £50 float. */
async function openTillIfAsked(page) {
  if (await top(page).getByText('Open the till').waitFor({ timeout: 1500 }).then(() => true, () => false)) {
    await pin(page, '5000');
    await top(page).getByRole('button', { name: 'Open till' }).click();
  }
}

test('drivers: deliveries go out with a driver who sees them; cash and card taken on return; one brought back', async () => {
  const owner = { device: shop.owner.device, token: shop.owner.token };
  await call('POST', '/api/staff', owner, { name: 'Dan Driver', role: 'Driver', pin: '6181' });
  const bottle = (await call('GET', '/api/menu', owner)).products.find(p => p.name.en === 'Bottle 1.5L');
  const delivery = async (name, line1) => {
    const id = crypto.randomUUID();
    const order = await call('POST', '/api/orders', owner, {
      orderId: id, idempotencyKey: id, source: 'Phone', type: 'Delivery',
      lines: [{ productId: bottle.id, variantId: bottle.variants[0].id, quantity: 5 }], // 5 × £2.99 + £1.50 delivery = £16.45
      contact: { name, phone: '07700 900123', address: { line1, line2: null, town: 'Leeds', postcode: 'LS2 9JT' }, notes: null },
      delivery: { distanceMiles: 1.3 },
    });
    await call('POST', `/api/orders/${order.id}/send-to-kitchen`, owner, {});
    return order;
  };
  const a = await delivery('Amy Cash', '1 Park Lane');
  const b = await delivery('Bob Card', '2 Mill Road');
  const c = await delivery('Cat Away', '3 Hill Street');

  const page = await tillPage(shop.till1);
  await login(page, '5931');
  await page.getByRole('button', { name: 'Drivers' }).click();
  await top(page).getByText('Tap the orders, then the driver:').waitFor();
  for (const o of [a, b, c]) await top(page).getByRole('button', { name: `Order ${o.number}` }).click();
  await top(page).getByText('Send 3 with:').waitFor();
  await shot(page, 'ui-drivers-send');
  await top(page).getByRole('button', { name: 'Dan Driver' }).click();
  await page.getByText('3 deliveries out with Dan Driver.').waitFor();
  const dan = top(page).getByLabel('Dan Driver', { exact: true });
  await dan.getByText('3 out · collect £49.35').waitFor();

  // Dan logs in on the other till and sees his deliveries, with what to collect.
  const driver = await tillPage(shop.till2);
  await driver.getByText('Enter your PIN').waitFor(); // (no order ticket on a driver's screen, so not login())
  await pin(driver, '6181');
  await driver.getByRole('button', { name: 'Log in' }).click();
  await driver.getByText('My deliveries · Dan Driver').waitFor();
  await driver.getByLabel(`Order ${a.number}`).getByText('1 Park Lane, Leeds, LS2 9JT').waitFor();
  await driver.getByLabel(`Order ${a.number}`).getByText('COLLECT £16.45').waitFor();
  assert.equal(await driver.locator('.driver-card').count(), 3);
  await shot(driver, 'ui-driver-screen');

  // Back at the shop: Amy paid cash, Bob paid by card on Dan's machine, Cat was not home.
  await dan.getByLabel(`Order ${a.number}`).getByRole('button', { name: 'Cash £16.45' }).click();
  await openTillIfAsked(page);
  await page.getByText(`Order ${a.number} delivered: £16.45 cash from Dan Driver.`).waitFor();
  await dan.getByLabel(`Order ${b.number}`).getByRole('button', { name: 'Card' }).click();
  await page.getByText(`Order ${b.number} delivered: £16.45 card from Dan Driver.`).waitFor();
  await dan.getByLabel(`Order ${c.number}`).getByRole('button', { name: 'Back' }).click();
  await top(page).getByRole('button', { name: 'Nobody home' }).click();
  await page.getByText(`Order ${c.number} is back in the shop.`).waitFor();
  await top(page).getByRole('button', { name: `Order ${c.number}` }).waitFor(); // ready to go out again
  await page.waitForTimeout(1500); // give the live update time to arrive
  assert.equal(await page.getByText(`Order ${c.number} is ready`).count(), 0); // not announced as new from the kitchen

  // The driver's screen empties by itself; the day shows what Dan handed in.
  await driver.getByText('No deliveries with you.').waitFor();
  const row = top(page).locator('.drivers-day tr', { hasText: 'Dan Driver' });
  await row.getByRole('cell', { name: '£16.45' }).first().waitFor();
  assert.deepEqual(await row.locator('td').allInnerTexts(), ['Dan Driver', '2', '0', '£16.45', '£16.45']);
  await shot(page, 'ui-drivers-back');
  await top(page).getByRole('button', { name: 'Done' }).click();

  const today = await ordersToday();
  assert.deepEqual([a, b, c].map(o => today.find(t => t.id === o.id)).map(o => [o.status, o.driverId !== null && o.driverId !== undefined]),
    [['Completed', true], ['Completed', true], ['Ready', false]]);
  assert.deepEqual([...page.errors, ...driver.errors], []);
  await driver.context().close();
  await page.context().close();
});

test('split bill at a table: one pays for their item, the rest is shared equally to the penny', async () => {
  const page = await tillPage(shop.till1);
  await login(page, '5931');
  await page.getByRole('button', { name: 'Eat in', exact: true }).click();
  await pick(page, 'Starters', 'Onion rings');           // £2.99
  await pick(page, 'Desserts', 'Cheesecake');            // £3.99
  await pick(page, 'Desserts', 'Chocolate fudge cake');  // £3.99
  await pick(page, 'Desserts', 'Ice cream tub');         // £4.99
  await page.locator('.total-total', { hasText: '£15.96' }).waitFor();
  await page.getByRole('button', { name: 'Pay £15.96' }).click();
  await openTillIfAsked(page);

  // One guest pays for their cheesecake only.
  await top(page).getByRole('radio', { name: 'By items' }).click();
  await top(page).getByText('Choose the items first.').waitFor();
  await top(page).getByRole('button', { name: '1 Cheesecake' }).click();
  await top(page).getByText('These items: £3.99').waitFor();
  await top(page).getByRole('button', { name: 'Card', exact: true }).click();
  await shot(page, 'ui-split-items');
  await top(page).getByRole('button', { name: 'Card approved £3.99' }).click();
  await page.getByText('£3.99 taken. £11.97 still to pay.').waitFor();
  assert.equal(await top(page).getByRole('button', { name: '1 Cheesecake' }).isDisabled(), true); // paid once only

  // The other two share the rest: £11.97 → £5.99 and £5.98.
  await top(page).getByRole('radio', { name: 'Equal shares' }).click();
  await top(page).getByText('Person 1 of 2 pays £5.99').waitFor();
  await top(page).getByText('Then: £5.98').waitFor();
  await top(page).getByRole('button', { name: 'Cash', exact: true }).click();
  await shot(page, 'ui-split-equal');
  await top(page).getByRole('button', { name: '£10.00' }).click();
  await page.locator('.change-amount', { hasText: '£4.01' }).waitFor();
  await top(page).getByRole('button', { name: 'Done' }).click();
  await top(page).getByText('Person 2 of 2 pays £5.98').waitFor();
  await top(page).getByRole('button', { name: 'Card', exact: true }).click();
  await top(page).getByRole('button', { name: 'Card approved £5.98' }).click();
  await page.getByText('Paid. Receipt printing.').waitFor();

  const order = (await ordersToday()).at(-1);
  assert.equal(order.total.minorUnits, 1596);
  assert.equal(order.balance.minorUnits, 0);
  const payments = await call('GET', `/api/orders/${order.id}/payments`, { device: shop.owner.device, token: shop.owner.token });
  const cheesecake = order.lines.find(l => l.name.en === 'Cheesecake').id;
  assert.deepEqual(payments.payments.map(p => [p.method, p.amount.minorUnits, p.forLineIds]), [['Card', 399, [cheesecake]], ['Cash', 599, []], ['Card', 598, []]]);
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('owner opens sales reports: totals match the hub, items, hours, staff; a table saves as a spreadsheet', async () => {
  const owner = { device: shop.owner.device, token: shop.owner.token };
  // A completed sale of its own: a cheesecake, paid in cash.
  const cake = (await call('GET', '/api/menu', owner)).products.find(p => p.name.en === 'Cheesecake');
  const id = crypto.randomUUID();
  const sale = await call('POST', '/api/orders', owner, { orderId: id, idempotencyKey: id, source: 'Counter', type: 'Takeaway', lines: [{ productId: cake.id, variantId: cake.variants[0].id, quantity: 1 }] });
  await call('POST', '/api/shifts/open', owner, { openingFloatPence: 0 }).catch(() => {}); // already open is fine
  await call('POST', `/api/orders/${sale.id}/payments/cash`, owner, { tenderedPence: 399 });
  await call('POST', `/api/orders/${sale.id}/status`, owner, { status: 'Completed' });
  const expected = await call('GET', '/api/reports/sales', owner);
  assert.ok(expected.summary.orders > 0);
  const page = await tillPage(shop.till2);
  await login(page, '4826');
  await page.getByRole('button', { name: 'More', exact: true }).click();
  await shot(page, 'ui-more-reports');
  await top(page).getByRole('button', { name: 'Sales reports' }).click();

  const kpi = label => top(page).locator('.kpi', { hasText: label }).locator('.kpi-value');
  assert.equal(await kpi('Orders').innerText(), String(expected.summary.orders));
  const pounds = m => `£${(m.minorUnits / 100).toFixed(2)}`;
  assert.equal(await kpi('Sales').first().innerText(), pounds(expected.summary.sales));
  await shot(page, 'ui-reports-summary');

  await top(page).getByRole('tab', { name: 'Items' }).click();
  await top(page).getByRole('cell', { name: 'Cheesecake', exact: true }).waitFor();
  await top(page).getByRole('tab', { name: 'Hours' }).click();
  await top(page).locator('.bar-row').first().waitFor();
  await shot(page, 'ui-reports-hours');
  await top(page).getByRole('tab', { name: 'Staff' }).click();
  await top(page).getByRole('cell', { name: 'Olivia Owner' }).waitFor();

  // The items table as a spreadsheet file.
  await top(page).getByRole('tab', { name: 'Items' }).click();
  const [download] = await Promise.all([page.waitForEvent('download'), top(page).getByRole('button', { name: 'Save as spreadsheet' }).click()]);
  const csv = fs.readFileSync(await download.path(), 'utf8').replace(/^\uFEFF/, '');
  assert.equal(csv.split('\r\n')[0], 'Item,Quantity,Sales');
  const cakes = expected.items.find(i => i.name === 'Cheesecake');
  assert.ok(csv.includes(`\r\nCheesecake,${cakes.quantity},${(cakes.sales.minorUnits / 100).toFixed(2)}\r\n`));
  assert.match(download.suggestedFilename(), /^sales-items-\d{4}-\d{2}-\d{2}-to-\d{4}-\d{2}-\d{2}\.csv$/);

  // Yesterday had no sales in this test shop.
  await top(page).getByRole('button', { name: 'Yesterday' }).click();
  await top(page).getByText('No sales in these days.').waitFor();
  await top(page).getByRole('tab', { name: 'Summary' }).click();
  assert.equal(await kpi('Orders').innerText(), '0');
  assert.deepEqual(page.errors, []);
  await page.context().close();
});

test('kitchen screen: register it, orders appear live, Start, new items marked, Ready tells the till, Recall', async () => {
  const owner = { device: shop.owner.device, token: shop.owner.token };
  await call('POST', '/api/staff', owner, { name: 'Kofi Kitchen', role: 'Kitchen', pin: '3185' });
  const { code } = await call('POST', '/api/devices/pairing-code', owner);

  // A new device registers itself as a kitchen screen.
  const context = await browser.newContext({ viewport: { width: 1366, height: 768 } });
  const kitchen = await context.newPage();
  kitchen.errors = [];
  kitchen.on('pageerror', e => kitchen.errors.push(e.message));
  open.add(kitchen);
  await kitchen.goto(`${base}/pos/`);
  await kitchen.getByText('Register this device').waitFor();
  await kitchen.getByRole('radio', { name: 'Kitchen screen' }).click();
  assert.equal(await kitchen.getByLabel('Name').inputValue(), 'Kitchen 1');
  await kitchen.getByLabel('Code').fill(code);
  await kitchen.getByRole('button', { name: 'Register' }).click();
  await kitchen.getByText('Enter your PIN').waitFor();
  await pin(kitchen, '3185');
  await kitchen.getByRole('button', { name: 'Log in' }).click();
  await kitchen.locator('.kds-board').waitFor(); // kitchen staff get the kitchen board, not the till
  await kitchen.locator('.live-on').waitFor();

  // The till sends an order with a note.
  const till = await tillPage(shop.till1);
  await login(till, '5931');
  await pick(till, 'Kebabs', 'Doner kebab');
  await top(till).getByRole('button', { name: /Large/ }).click();
  await top(till).getByRole('button', { name: /Chilli/ }).click();
  await top(till).getByLabel('Note for the kitchen').fill('Well done');
  await top(till).getByRole('button', { name: /^Add/ }).click();
  await till.getByRole('button', { name: 'Send', exact: true }).click();
  await till.locator('.lines-empty').waitFor();
  const order = (await call('GET', '/api/orders', owner)).at(-1);
  const card = kitchen.locator('.kds-card', { has: kitchen.locator('.kds-number', { hasText: new RegExp(`^${order.number}$`) }) });
  await card.waitFor();
  await card.getByText('Large Doner kebab').waitFor();
  await card.getByText('+ Chilli').waitFor();
  await card.getByText('Well done').waitFor();

  // Start: the card turns amber and the order is "Preparing".
  await card.getByRole('button', { name: 'Start' }).click();
  await kitchen.locator('.kds-card.kds-preparing', { hasText: order.number }).waitFor();
  assert.equal((await call('GET', `/api/orders/${order.id}`, owner)).status, 'Preparing');

  // More items from the till show as NEW on the same card.
  await till.getByRole('button', { name: /^Orders/ }).click();
  await top(till).getByRole('button', { name: new RegExp(`^${order.number}`) }).click();
  await pick(till, 'Drinks', 'Can 330ml');
  await till.getByRole('button', { name: 'Send new' }).click();
  await card.locator('.kds-line', { hasText: 'Can 330ml' }).getByText('NEW').waitFor();
  assert.equal(await card.locator('.kds-line', { hasText: 'Large Doner kebab' }).locator('.kds-flag-new').count(), 0); // part of the original order
  await shot(kitchen, 'ui-kitchen');

  // Ready: off the board, into the Ready bar, and the till is told.
  await card.getByRole('button', { name: 'Ready' }).click();
  await card.waitFor({ state: 'detached' });
  await kitchen.locator('.kds-ready-chip', { hasText: order.number }).waitFor();
  await till.getByText(`Order ${order.number} is ready.`).waitFor();

  // Recall: back on the board.
  await kitchen.locator('.kds-ready-chip', { hasText: order.number }).click();
  await top(kitchen).getByRole('button', { name: 'Recall' }).click();
  await kitchen.locator('.kds-card.kds-preparing', { hasText: order.number }).waitFor();
  assert.equal((await call('GET', `/api/orders/${order.id}`, owner)).status, 'Preparing');
  await kitchen.locator('.kds-card', { hasText: order.number }).getByRole('button', { name: 'Ready' }).click();
  assert.deepEqual([...kitchen.errors, ...till.errors], []);
  await till.context().close();
  await context.close();
});

test('every printout was made: kitchen tickets, additions, the void slip and receipts', async () => {
  const jobs = await call('GET', '/api/print-jobs', { device: shop.owner.device, token: shop.owner.token });
  const kinds = new Set(jobs.map(j => j.job.kind));
  for (const kind of ['KitchenNew', 'KitchenAdditional', 'KitchenVoid', 'Receipt']) assert.ok(kinds.has(kind), kind);
  assert.ok(jobs.every(j => j.status === 'Printed'), 'all printed');
  assert.ok(jobs.every(j => fs.existsSync(j.output)), 'files exist');
});
