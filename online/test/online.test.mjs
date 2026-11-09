// Tests of the online ordering service, run on this computer: node --test online/test
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { after, before, beforeEach, test } from 'node:test';
import { start } from '../dev/local.mjs';
import { deliveryQuote, isOpen, miles, postcode, priceBasket, ukPhone } from '../public/rules.js';

const KEY = 'test-shop-key-0123456789abcdef0123456789';
const ALWAYS = Object.fromEntries(['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'].map(d => [d, [{ open: '00:00', close: '00:00' }]]));

// Postcodes near Birmingham B12 (the shop) and one far away, as postcodes.io would answer.
const PLACES = { 'B12 9QX': [52.4651, -1.8822], 'B12 0AA': [52.4720, -1.8900], 'M1 1AE': [53.4794, -2.2453] };
let postcodes;
let service;
let base;

const menu = {
  categories: [{ id: 'c-pizza', name: 'Pizzas' }, { id: 'c-drink', name: 'Drinks' }],
  groups: [
    { id: 'g-top', name: 'Extra toppings', min: 0, max: 2, options: [{ id: 'o-cheese', name: 'Extra cheese', pricePence: 100, soldOut: false }, { id: 'o-ham', name: 'Ham', pricePence: 120, soldOut: true }] },
    { id: 'g-sauce', name: 'Sauce', min: 1, max: 1, options: [{ id: 'o-garlic', name: 'Garlic', pricePence: 0, soldOut: false }, { id: 'o-chilli', name: 'Chilli', pricePence: 0, soldOut: false }] },
  ],
  products: [
    { id: 'p-marg', categoryId: 'c-pizza', name: 'Margherita', description: 'Tomato and mozzarella', allergens: ['gluten', 'milk'], soldOut: false, groupIds: ['g-top'],
      variants: [{ id: 'v-12', name: '12"', pricePence: 999, soldOut: false }, { id: 'v-16', name: '16"', pricePence: 1299, soldOut: true }] },
    { id: 'p-kebab', categoryId: 'c-pizza', name: 'Doner', description: null, allergens: [], soldOut: false, groupIds: ['g-sauce'],
      variants: [{ id: 'v-reg', name: 'Regular', pricePence: 750, soldOut: false }] },
    { id: 'p-can', categoryId: 'c-drink', name: 'Can', description: null, allergens: [], soldOut: false, groupIds: [], variants: [{ id: 'v-can', name: 'Regular', pricePence: 150, soldOut: false }] },
  ],
};

const info = (overrides = {}) => ({
  name: 'Test Takeaway', address: ['1 High Street', 'Birmingham'], postcode: 'B12 9QX', phone: '0121 000 0000', hours: ALWAYS,
  collection: { enabled: true, minutes: 20 },
  delivery: {
    enabled: true, minutes: 45, roadFactor: 1.3, shopLocation: { latitude: 52.4651, longitude: -1.8822 },
    table: { bands: [{ fromMiles: 0, toMiles: 2, feePence: 150 }, { fromMiles: 2, toMiles: 5, feePence: 300 }], maximumMiles: 5, minimumOrderPence: 1000, freeOverPence: 3000 },
  },
  allergenNote: 'Ask us about allergens.',
  ...overrides,
});

async function call(method, path, body, key) {
  const res = await fetch(base + path, {
    method,
    headers: { 'Content-Type': 'application/json', ...(key ? { Authorization: `Bearer ${key}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

const hub = (method, path, body) => call(method, path, body, KEY);
const goOnline = (accepting = true) => hub('POST', '/hub/poll', { accepting, updates: [] });
const collection = (lines, customer = { name: 'Sam Patel', phone: '07700 900123' }) => call('POST', '/api/orders', { type: 'Collection', customer, lines });

before(async () => {
  postcodes = createServer((req, res) => {
    const code = decodeURIComponent(req.url.split('/').pop());
    const place = PLACES[code];
    res.writeHead(place ? 200 : 404, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(place ? { status: 200, result: { postcode: code, latitude: place[0], longitude: place[1] } } : { status: 404, error: 'Postcode not found' }));
  });
  await new Promise(r => postcodes.listen(0, '127.0.0.1', r));
});

after(() => new Promise(r => postcodes.close(r)));

beforeEach(async () => {
  await service?.close();
  service = await start({ shopKey: KEY, postcodeService: `http://127.0.0.1:${postcodes.address().port}` });
  base = service.url;
});

after(async () => { await service?.close(); });

// ---------- Rules ----------

test('delivery charges follow the hub rules: bands, last band includes its end, minimum order, free over', () => {
  const table = { bands: [{ fromMiles: 0, toMiles: 2, feePence: 150 }, { fromMiles: 2, toMiles: 5, feePence: 300, minimumOrderPence: 1500 }], maximumMiles: 5, minimumOrderPence: 1000, freeOverPence: 3000 };
  assert.deepEqual(deliveryQuote(table, 1.9, 1200), { status: 'Ok', feePence: 150, minimumOrderPence: 1000, free: false });
  assert.equal(deliveryQuote(table, 2, 1600).feePence, 300);
  assert.equal(deliveryQuote(table, 5, 1600).status, 'Ok');
  assert.equal(deliveryQuote(table, 5.1, 1600).status, 'TooFar');
  assert.equal(deliveryQuote(table, 3, 1200).status, 'BelowMinimum');
  assert.deepEqual(deliveryQuote(table, 1, 3000), { status: 'Ok', feePence: 0, minimumOrderPence: 1000, free: true });
  const perMile = { ratePerMilePence: 100, minimumFeePence: 200, maximumFeePence: 450, maximumMiles: 6 };
  assert.equal(deliveryQuote(perMile, 1, 0).feePence, 200);
  assert.equal(deliveryQuote(perMile, 3.25, 0).feePence, 325);
  assert.equal(deliveryQuote(perMile, 5.5, 0).feePence, 450);
});

test('distance is straight line times the road factor, to 0.1 mile, like the hub', () => {
  assert.equal(miles({ latitude: 52.4651, longitude: -1.8822 }, { latitude: 52.4651, longitude: -1.8822 }), 0);
  // 0.59 miles in a straight line × 1.3 = 0.77, shown as 0.8 (the same sum the hub does).
  assert.equal(miles({ latitude: 52.4651, longitude: -1.8822 }, { latitude: 52.4720, longitude: -1.8900 }), 0.8);
  assert.equal(miles({ latitude: 52.4651, longitude: -1.8822 }, { latitude: 52.4720, longitude: -1.8900 }, 1), 0.6);
});

test('postcodes and UK phone numbers are tidied or refused', () => {
  assert.equal(postcode('b129qx'), 'B12 9QX');
  assert.equal(postcode(' sw1a 1aa '), 'SW1A 1AA');
  assert.equal(postcode('12345'), null);
  assert.equal(ukPhone('07700 900123'), '07700900123');
  assert.equal(ukPhone('+44 (0121) 496 0000'.replace('(0121)', '121')), '01214960000');
  assert.equal(ukPhone('123'), null);
  assert.equal(ukPhone('+33 1 23 45 67 89'), null);
});

test('opening hours use UK time, including past midnight and British Summer Time', () => {
  const hours = { fri: [{ open: '17:00', close: '02:00' }] };
  assert.equal(isOpen(hours, new Date('2026-11-13T18:00:00Z')), true);   // Friday 18:00 UK (winter: UTC)
  assert.equal(isOpen(hours, new Date('2026-11-14T01:30:00Z')), true);   // Saturday 01:30, still Friday night
  assert.equal(isOpen(hours, new Date('2026-11-14T02:30:00Z')), false);
  assert.equal(isOpen(hours, new Date('2026-07-10T16:30:00Z')), true);   // Friday 17:30 UK in summer (UTC+1)
  assert.equal(isOpen(hours, new Date('2026-07-10T15:30:00Z')), false);  // 16:30 UK
});

test('the basket is checked and priced against the menu', () => {
  const ok = priceBasket(menu, [{ productId: 'p-marg', variantId: 'v-12', quantity: 2, optionIds: ['o-cheese'] }, { productId: 'p-kebab', variantId: 'v-reg', quantity: 1, optionIds: ['o-garlic'] }]);
  assert.deepEqual(ok.problems, []);
  assert.equal(ok.goodsPence, (999 + 100) * 2 + 750);
  assert.match(priceBasket(menu, [{ productId: 'p-marg', variantId: 'v-16', quantity: 1 }]).problems[0], /sold out/);
  assert.match(priceBasket(menu, [{ productId: 'p-kebab', variantId: 'v-reg', quantity: 1 }]).problems[0], /choose 1 sauce/);
  assert.match(priceBasket(menu, [{ productId: 'p-marg', variantId: 'v-12', quantity: 1, optionIds: ['o-ham'] }]).problems[0], /Ham has sold out/);
  assert.match(priceBasket(menu, [{ productId: 'p-marg', variantId: 'v-12', quantity: 1, optionIds: ['o-garlic'] }]).problems[0], /no longer available/);
  assert.match(priceBasket(menu, [{ productId: 'p-marg', variantId: 'v-12', quantity: 0 }]).problems[0], /quantity/);
  assert.match(priceBasket(menu, [{ productId: 'nope', variantId: 'v', quantity: 1 }]).problems[0], /no longer on the menu/);
  assert.match(priceBasket(menu, []).problems[0], /empty/);
});

// ---------- The service ----------

test('before the shop publishes, the website says ordering is not set up', async () => {
  const shop = await call('GET', '/api/shop');
  assert.equal(shop.body.configured, false);
  assert.equal((await collection([{ productId: 'p-can', variantId: 'v-can', quantity: 1 }])).status, 409);
});

test('only the hub, with the shop key, can publish or collect orders', async () => {
  assert.equal((await call('PUT', '/hub/publish', { info: info(), menu })).status, 401);
  assert.equal((await call('POST', '/hub/poll', {}, 'wrong-key-0123456789abcdef0123456789')).status, 401);
  assert.equal((await hub('PUT', '/hub/publish', { info: info(), menu })).status, 200);
  assert.equal((await hub('PUT', '/hub/publish', { info: {}, menu })).status, 400);
});

test('orders are taken only while the shop takes them, its hub is online and it is open', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  const line = [{ productId: 'p-can', variantId: 'v-can', quantity: 1 }];

  // Published, but the hub has not said it takes website orders.
  assert.equal((await call('GET', '/api/shop')).body.ordering.reason, 'We are not taking online orders right now.');
  assert.equal((await collection(line)).status, 409);

  await goOnline(true);
  assert.equal((await call('GET', '/api/shop')).body.ordering.open, true);
  assert.equal((await collection(line)).status, 201);

  // The hub has not checked in for 3 minutes (shop PC off): no orders.
  await service.env.DB.prepare("UPDATE shop SET hub_seen_at = '2020-01-01T00:00:00.000Z'").run();
  assert.equal((await call('GET', '/api/shop')).body.ordering.reason, 'We cannot take online orders right now. Please phone us.');
  assert.equal((await collection(line)).status, 409);

  // Closed.
  await hub('PUT', '/hub/publish', { info: info({ hours: {} }), menu });
  await goOnline(true);
  assert.equal((await call('GET', '/api/shop')).body.ordering.reason, 'We are closed now.');
});

test('a collection order goes to the hub until the hub says it has it, and the customer follows it', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();

  const placed = await collection([{ productId: 'p-marg', variantId: 'v-12', quantity: 1, optionIds: ['o-cheese'], note: 'well done' }], { name: ' Sam Patel ', phone: '+44 7700 900123', notes: 'Ring the bell' });
  assert.equal(placed.status, 201);
  assert.equal(placed.body.totalPence, 1099);

  // The hub collects it, again and again until it reports it received (nothing is lost if it crashes in between).
  for (let i = 0; i < 2; i++) {
    const { body } = await goOnline();
    assert.equal(body.orders.length, 1);
    const order = body.orders[0];
    assert.equal(order.id, placed.body.id);
    assert.deepEqual(order.customer, { name: 'Sam Patel', phone: '07700900123', notes: 'Ring the bell' });
    assert.deepEqual(order.lines, [{ productId: 'p-marg', variantId: 'v-12', quantity: 1, optionIds: ['o-cheese'], note: 'well done', unitPence: 1099 }]);
  }

  // The customer's status page needs the secret link.
  const path = `/api/orders/${placed.body.id}`;
  assert.equal((await call('GET', `${path}?t=wrong`)).status, 404);
  assert.equal((await call('GET', `${path}?t=${placed.body.token}`)).body.status, 'Placed');

  await hub('POST', '/hub/poll', { accepting: true, updates: [{ id: placed.body.id, status: 'Received', number: '014' }] });
  assert.equal((await goOnline()).body.orders.length, 0);
  await hub('POST', '/hub/poll', { accepting: true, updates: [{ id: placed.body.id, status: 'Accepted', readyAt: '2026-11-09T19:45:00Z', totalPence: 1099 }] });
  const seen = (await call('GET', `${path}?t=${placed.body.token}`)).body;
  assert.equal(seen.status, 'Accepted');
  assert.equal(seen.number, '014');
  assert.equal(seen.readyAt, '2026-11-09T19:45:00Z');

  await hub('POST', '/hub/poll', { accepting: true, updates: [{ id: placed.body.id, status: 'Rejected', reason: 'Out of dough' }, { id: placed.body.id, status: 'Bogus' }] });
  assert.equal((await call('GET', `${path}?t=${placed.body.token}`)).body.reason, 'Out of dough');
});

test('name, phone and a valid basket are required', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const can = [{ productId: 'p-can', variantId: 'v-can', quantity: 1 }];
  assert.equal((await collection(can, { name: '', phone: '07700900123' })).body.message, 'Enter your name.');
  assert.match((await collection(can, { name: 'Sam', phone: '123' })).body.message, /UK phone number/);
  const bad = await collection([{ productId: 'p-kebab', variantId: 'v-reg', quantity: 1 }]);
  assert.equal(bad.status, 400);
  assert.deepEqual(bad.body.problems, ['Doner: choose 1 sauce.']);
  assert.equal((await call('POST', '/api/orders', { type: 'Pigeon', customer: { name: 'Sam', phone: '07700900123' }, lines: can })).body.message, 'Choose collection or delivery.');
});

test('delivery: the postcode decides the fee, too far and minimum order are refused', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const check = await call('POST', '/api/delivery-check', { postcode: 'b12 0aa', goodsPence: 1200 });
  assert.equal(check.body.postcode, 'B12 0AA');
  assert.equal(check.body.status, 'Ok');
  assert.equal(check.body.feePence, 150);
  assert.equal((await call('POST', '/api/delivery-check', { postcode: 'M1 1AE', goodsPence: 1200 })).body.status, 'TooFar');
  assert.match((await call('POST', '/api/delivery-check', { postcode: 'ZZ9 9ZZ' })).body.message, /could not find ZZ9 9ZZ/);
  assert.match((await call('POST', '/api/delivery-check', { postcode: 'hello' })).body.message, /full UK postcode/);

  const customer = { name: 'Sam Patel', phone: '07700900123', address: '12 Long Road', postcode: 'B12 0AA' };
  const lines = [{ productId: 'p-marg', variantId: 'v-12', quantity: 2 }];
  const order = await call('POST', '/api/orders', { type: 'Delivery', customer, lines });
  assert.equal(order.status, 201);
  assert.equal(order.body.totalPence, 999 * 2 + 150);
  const collected = (await goOnline()).body.orders[0];
  assert.equal(collected.deliveryMiles, check.body.miles);
  assert.equal(collected.customer.postcode, 'B12 0AA');

  assert.match((await call('POST', '/api/orders', { type: 'Delivery', customer, lines: [{ productId: 'p-can', variantId: 'v-can', quantity: 1 }] })).body.message, /minimum order for delivery/);
  assert.match((await call('POST', '/api/orders', { type: 'Delivery', customer: { ...customer, postcode: 'M1 1AE' }, lines })).body.message, /too far/);
  assert.match((await call('POST', '/api/orders', { type: 'Delivery', customer: { ...customer, address: '' }, lines })).body.message, /house number and street/);
});

test('at most 5 orders from one visitor in 30 minutes', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const can = [{ productId: 'p-can', variantId: 'v-can', quantity: 1 }];
  for (let i = 0; i < 5; i++) assert.equal((await collection(can)).status, 201);
  const sixth = await collection(can);
  assert.equal(sixth.status, 429);
  assert.match(sixth.body.message, /phone us/);
});

test('an order the shop never collected is shown as not received after 10 minutes', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const placed = await collection([{ productId: 'p-can', variantId: 'v-can', quantity: 1 }]);
  await service.env.DB.prepare('UPDATE orders SET created_at = ? WHERE id = ?').bind(new Date(Date.now() - 11 * 60000).toISOString(), placed.body.id).run();
  assert.equal((await call('GET', `/api/orders/${placed.body.id}?t=${placed.body.token}`)).body.status, 'NotReceived');
});

test('orders older than 30 days are deleted (customers\' details are not kept)', async () => {
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const placed = await collection([{ productId: 'p-can', variantId: 'v-can', quantity: 1 }]);
  await service.env.DB.prepare('UPDATE orders SET created_at = ? WHERE id = ?').bind('2020-01-01T00:00:00.000Z', placed.body.id).run();
  await goOnline();
  assert.equal(await service.env.DB.prepare('SELECT COUNT(*) AS n FROM orders').first('n'), 0);
});

test('unknown addresses and oversized requests are refused politely', async () => {
  assert.equal((await call('GET', '/api/nothing')).status, 404);
  await hub('PUT', '/hub/publish', { info: info(), menu });
  await goOnline();
  const big = await fetch(`${base}/api/orders`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: 'x'.repeat(70 * 1024) });
  assert.equal(big.status, 413);
  const notJson = await fetch(`${base}/api/delivery-check`, { method: 'POST', body: '{nope' });
  assert.equal(notJson.status, 400);
});
