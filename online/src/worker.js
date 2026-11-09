// The online ordering service (Cloudflare Worker + D1 database, free plan).
// Customers: GET /api/shop, GET /api/menu, POST /api/delivery-check, POST /api/orders, GET /api/orders/{id}?t=token.
// The shop's hub (with the shop key): PUT /hub/publish, POST /hub/poll.
// The website pages themselves are static files (public/), served by Cloudflare before this code runs.
import { deliveryQuote, isOpen, miles, postcode, priceBasket, ukPhone } from '../public/rules.js';

const MAX_BODY = 64 * 1024;        // a customer's order is a few KB
const MAX_PUBLISH = 2 * 1024 * 1024; // a big menu is well under this
const HUB_ONLINE_SECONDS = 120;     // orders are taken only while the hub has checked in this recently
const NOT_RECEIVED_MINUTES = 10;    // an order the shop has not collected by then is shown as not received
const KEEP_DAYS = 30;               // orders (with customers' details) are deleted after this
const ATTEMPTS = { max: 5, minutes: 30 }; // at most 5 orders per visitor per 30 minutes

const STATUSES = ['Placed', 'Received', 'Accepted', 'Ready', 'OutForDelivery', 'Completed', 'Rejected', 'Cancelled'];
const HUB_STATUSES = new Set(STATUSES.slice(1));

class Problem extends Error {
  constructor(status, message, extra = {}) {
    super(message);
    this.status = status;
    this.extra = extra;
  }
}

const json = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), {
  status,
  headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...headers },
});

async function readJson(request, limit = MAX_BODY) {
  const declared = Number(request.headers.get('Content-Length') ?? 0);
  if (declared > limit) throw new Problem(413, 'Too much data.');
  const text = await request.text();
  if (text.length > limit) throw new Problem(413, 'Too much data.');
  try {
    return JSON.parse(text);
  } catch {
    throw new Problem(400, 'Not valid JSON.');
  }
}

const now = () => new Date().toISOString();
const minutesAgo = m => new Date(Date.now() - m * 60000).toISOString();

function randomId(bytes = 16) {
  const b = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function sha256(text) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map(x => x.toString(16).padStart(2, '0')).join('');
}

/** Compares two secrets without leaking how much of them matched. */
async function sameSecret(a, b) {
  const [x, y] = await Promise.all([sha256(`k:${a}`), sha256(`k:${b}`)]);
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x.charCodeAt(i) ^ y.charCodeAt(i);
  return diff === 0;
}

const clean = (value, max) => (typeof value === 'string' ? value.replace(/[\u0000-\u001f\u007f]/g, ' ').trim().slice(0, max) : '');

// ---------- Shop ----------

async function loadShop(env) {
  const row = await env.DB.prepare('SELECT info_json, menu_json, published_at, hub_seen_at, accepting FROM shop WHERE id = 1').first();
  if (!row) return null;
  return { info: JSON.parse(row.info_json), menu: JSON.parse(row.menu_json), publishedAt: row.published_at, hubSeenAt: row.hub_seen_at, accepting: !!row.accepting };
}

/** Whether orders can be placed now, and if not, why (shown on the website). */
function ordering(shop, at = new Date()) {
  if (!shop) return { open: false, reason: 'Online ordering is not set up yet.' };
  const hubOnline = shop.hubSeenAt && at.getTime() - new Date(shop.hubSeenAt).getTime() < HUB_ONLINE_SECONDS * 1000;
  if (!shop.accepting) return { open: false, reason: 'We are not taking online orders right now.' };
  if (!hubOnline) return { open: false, reason: 'We cannot take online orders right now. Please phone us.' };
  if (!isOpen(shop.info.hours, at)) return { open: false, reason: 'We are closed now.' };
  return { open: true, reason: null };
}

function publicShop(shop) {
  const state = ordering(shop);
  if (!shop) return { configured: false, ordering: state };
  const i = shop.info;
  return {
    configured: true,
    name: i.name, address: i.address, postcode: i.postcode, phone: i.phone, hours: i.hours,
    collection: { enabled: !!i.collection?.enabled, minutes: i.collection?.minutes ?? 20 },
    delivery: i.delivery?.enabled
      ? { enabled: true, minutes: i.delivery.minutes ?? 45, maximumMiles: i.delivery.table.maximumMiles, minimumOrderPence: i.delivery.table.minimumOrderPence ?? null }
      : { enabled: false },
    allergenNote: i.allergenNote ?? null,
    ordering: state,
  };
}

// ---------- Delivery check (postcodes.io: free UK open data) ----------

async function locate(env, code) {
  const base = env.POSTCODE_SERVICE ?? 'https://api.postcodes.io';
  const response = await fetch(`${base}/postcodes/${encodeURIComponent(code)}`, { headers: { Accept: 'application/json' } });
  if (response.status === 404) return null;
  if (!response.ok) throw new Problem(503, 'The postcode check is not working right now. Please try again, or phone us.');
  const body = await response.json();
  return body?.result ? { latitude: body.result.latitude, longitude: body.result.longitude } : null;
}

async function checkDelivery(env, shop, postcodeText, goodsPence) {
  const d = shop.info.delivery;
  if (!d?.enabled) throw new Problem(400, 'We are not delivering online orders right now.');
  const code = postcode(postcodeText);
  if (!code) throw new Problem(400, 'Enter a full UK postcode, e.g. B12 9QX.');
  const place = await locate(env, code);
  if (!place) throw new Problem(400, `We could not find ${code}. Check the postcode.`);
  const distance = miles(d.shopLocation, place, d.roadFactor ?? 1.3);
  return { postcode: code, miles: distance, ...deliveryQuote(d.table, distance, goodsPence) };
}

// ---------- Orders ----------

async function placeOrder(request, env) {
  const shop = await loadShop(env);
  const state = ordering(shop);
  if (!state.open) throw new Problem(409, state.reason);

  const body = await readJson(request);
  const type = body.type === 'Delivery' ? 'Delivery' : body.type === 'Collection' ? 'Collection' : null;
  if (!type) throw new Problem(400, 'Choose collection or delivery.');
  if (type === 'Collection' && !shop.info.collection?.enabled) throw new Problem(400, 'We are not taking collection orders online right now.');

  const c = body.customer ?? {};
  const customer = { name: clean(c.name, 60), phone: ukPhone(c.phone), notes: clean(c.notes, 200) || null };
  if (customer.name.length < 2) throw new Problem(400, 'Enter your name.');
  if (!customer.phone) throw new Problem(400, 'Enter a UK phone number, so the shop can call you about your order.');

  const basket = priceBasket(shop.menu, body.lines);
  if (basket.problems.length) throw new Problem(400, basket.problems[0], { problems: basket.problems });

  let delivery = { feePence: 0, miles: null };
  if (type === 'Delivery') {
    customer.address = clean(c.address, 120);
    customer.town = clean(c.town, 60) || null;
    if (customer.address.length < 3) throw new Problem(400, 'Enter your house number and street.');
    const check = await checkDelivery(env, shop, c.postcode, basket.goodsPence);
    customer.postcode = check.postcode;
    if (check.status === 'TooFar') throw new Problem(400, `Sorry, ${check.postcode} is too far for us to deliver.`);
    if (check.status === 'BelowMinimum') throw new Problem(400, `The minimum order for delivery to ${check.postcode} is £${(check.minimumOrderPence / 100).toFixed(2)}.`);
    delivery = { feePence: check.feePence, miles: check.miles };
  }

  // A few orders per visitor at most, against pranks and robots. Only a hash of their address is kept, briefly.
  const who = await sha256(`${env.SHOP_KEY ?? ''}:${request.headers.get('CF-Connecting-IP') ?? 'local'}`);
  const recent = await env.DB.prepare('SELECT COUNT(*) AS n FROM attempts WHERE who = ? AND at > ?').bind(who, minutesAgo(ATTEMPTS.minutes)).first();
  if ((recent?.n ?? 0) >= ATTEMPTS.max) throw new Problem(429, 'Too many orders from here. Please phone us.');

  const id = crypto.randomUUID();
  const token = randomId();
  const at = now();
  const total = basket.goodsPence + delivery.feePence;
  await env.DB.batch([
    env.DB.prepare('INSERT INTO attempts (who, at) VALUES (?, ?)').bind(who, at),
    env.DB.prepare(`INSERT INTO orders (id, token, created_at, updated_at, status, type, customer_json, lines_json, goods_pence, delivery_pence, delivery_miles, total_pence)
      VALUES (?, ?, ?, ?, 'Placed', ?, ?, ?, ?, ?, ?, ?)`)
      .bind(id, token, at, at, type, JSON.stringify(customer), JSON.stringify(basket.lines), basket.goodsPence, delivery.feePence, delivery.miles, total),
  ]);
  return json({ id, token, totalPence: total }, 201);
}

function orderStatus(row) {
  let status = row.status;
  // Never collected by the shop's hub (e.g. its PC was switched off): tell the customer to phone.
  if (status === 'Placed' && new Date(row.created_at).getTime() < Date.now() - NOT_RECEIVED_MINUTES * 60000) status = 'NotReceived';
  return {
    id: row.id, status, type: row.type, number: row.hub_number, readyAt: row.ready_at, reason: row.reason,
    createdAt: row.created_at, goodsPence: row.goods_pence, deliveryPence: row.delivery_pence, totalPence: row.total_pence,
    lines: JSON.parse(row.lines_json),
  };
}

async function getOrder(env, id, token) {
  if (!id || !token) throw new Problem(404, 'Order not found.');
  const row = await env.DB.prepare('SELECT * FROM orders WHERE id = ?').bind(id).first();
  if (!row || !(await sameSecret(row.token, token))) throw new Problem(404, 'Order not found.');
  return json(orderStatus(row));
}

// ---------- The hub ----------

async function requireHub(request, env) {
  const header = request.headers.get('Authorization') ?? '';
  const key = header.startsWith('Bearer ') ? header.slice(7) : '';
  if (!env.SHOP_KEY || env.SHOP_KEY.length < 32) throw new Problem(503, 'The shop key is not set on the online service.');
  if (!key || !(await sameSecret(key, env.SHOP_KEY))) throw new Problem(401, 'Wrong shop key.');
}

async function publish(request, env) {
  await requireHub(request, env);
  const body = await readJson(request, MAX_PUBLISH);
  if (!body?.info?.name || !Array.isArray(body?.menu?.products) || !Array.isArray(body?.menu?.categories) || !Array.isArray(body?.menu?.groups)) {
    throw new Problem(400, 'The menu and shop details are incomplete.');
  }

  const at = now();
  await env.DB.prepare(`INSERT INTO shop (id, info_json, menu_json, published_at, hub_seen_at, accepting) VALUES (1, ?, ?, ?, ?, 0)
    ON CONFLICT(id) DO UPDATE SET info_json = excluded.info_json, menu_json = excluded.menu_json, published_at = excluded.published_at, hub_seen_at = excluded.hub_seen_at`)
    .bind(JSON.stringify(body.info), JSON.stringify(body.menu), at, at).run();
  return json({ publishedAt: at });
}

/**
 * The hub checks in every few seconds: it says whether the shop takes website orders, sends what happened to orders on
 * the till, and gets the new orders back. An order keeps being sent until the hub reports it received, so none is lost.
 */
async function poll(request, env) {
  await requireHub(request, env);
  const body = await readJson(request, MAX_BODY * 4);
  const at = now();
  const updates = Array.isArray(body.updates) ? body.updates.slice(0, 200) : [];
  const statements = [env.DB.prepare('UPDATE shop SET hub_seen_at = ?, accepting = ? WHERE id = 1').bind(at, body.accepting ? 1 : 0)];
  for (const u of updates) {
    if (typeof u?.id !== 'string' || !HUB_STATUSES.has(u.status)) continue;
    statements.push(env.DB.prepare(`UPDATE orders SET status = ?, hub_received = 1, updated_at = ?,
        hub_number = COALESCE(?, hub_number), ready_at = COALESCE(?, ready_at), reason = COALESCE(?, reason), total_pence = COALESCE(?, total_pence)
      WHERE id = ?`)
      .bind(u.status, at, clean(u.number, 20) || null, clean(u.readyAt, 40) || null, clean(u.reason, 200) || null,
        Number.isInteger(u.totalPence) ? u.totalPence : null, u.id));
  }

  // Customers' details are not kept longer than needed.
  statements.push(env.DB.prepare('DELETE FROM orders WHERE created_at < ?').bind(minutesAgo(KEEP_DAYS * 24 * 60)));
  statements.push(env.DB.prepare('DELETE FROM attempts WHERE at < ?').bind(minutesAgo(ATTEMPTS.minutes)));
  await env.DB.batch(statements);

  const waiting = await env.DB.prepare(`SELECT id, created_at, type, customer_json, lines_json, goods_pence, delivery_pence, delivery_miles, total_pence
    FROM orders WHERE hub_received = 0 AND status = 'Placed' ORDER BY created_at LIMIT 20`).all();
  return json({
    serverTime: at,
    orders: (waiting.results ?? []).map(r => ({
      id: r.id, createdAt: r.created_at, type: r.type, customer: JSON.parse(r.customer_json), lines: JSON.parse(r.lines_json),
      goodsPence: r.goods_pence, deliveryPence: r.delivery_pence, deliveryMiles: r.delivery_miles, totalPence: r.total_pence,
    })),
  });
}

// ---------- Router ----------

export async function handle(request, env) {
  const url = new URL(request.url);
  const path = url.pathname;
  const method = request.method;
  try {
    if (method === 'GET' && path === '/api/shop') return json(publicShop(await loadShop(env)));
    if (method === 'GET' && path === '/api/menu') {
      const shop = await loadShop(env);
      return shop ? json(shop.menu) : json({ categories: [], products: [], groups: [] });
    }

    if (method === 'POST' && path === '/api/delivery-check') {
      const shop = await loadShop(env);
      if (!shop) throw new Problem(409, 'Online ordering is not set up yet.');
      const body = await readJson(request);
      const goods = Number.isInteger(body.goodsPence) && body.goodsPence >= 0 ? body.goodsPence : 0;
      return json(await checkDelivery(env, shop, body.postcode, goods));
    }

    if (method === 'POST' && path === '/api/orders') return await placeOrder(request, env);
    const match = path.match(/^\/api\/orders\/([0-9a-f-]{36})$/);
    if (method === 'GET' && match) return await getOrder(env, match[1], url.searchParams.get('t'));
    if (method === 'PUT' && path === '/hub/publish') return await publish(request, env);
    if (method === 'POST' && path === '/hub/poll') return await poll(request, env);
    if (path.startsWith('/api/') || path.startsWith('/hub/')) throw new Problem(404, 'Not found.');
    return env.ASSETS ? env.ASSETS.fetch(request) : new Response('Not found', { status: 404 });
  } catch (e) {
    if (e instanceof Problem) return json({ message: e.message, ...e.extra }, e.status);
    console.error(e);
    return json({ message: 'Something went wrong. Please try again, or phone us.' }, 500);
  }
}

export default { fetch: (request, env) => handle(request, env) };
