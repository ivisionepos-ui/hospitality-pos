// Shared by the menu, checkout and order pages: making elements, calling the service, the basket kept in this browser.
import { pounds, priceBasket } from '/rules.js';

export const $ = id => document.getElementById(id);

/** Makes an element: h('p', { class: 'x', text: 'Hi' }, child…). */
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }

  for (const c of children.flat(9)) if (c != null && c !== false) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

/** Replaces an element's contents, skipping empty parts (replaceChildren would show null as "null"). */
export function put(el, ...children) {
  el.replaceChildren(...children.flat(9).filter(c => c != null && c !== false && c !== ''));
}

/** The shop's address on one line, without the postcode twice. */
export function addressLine(shop) {
  const lines = (shop.address ?? []).filter(Boolean);
  const pc = String(shop.postcode ?? '').replace(/\s/g, '').toUpperCase();
  const has = pc && lines.some(l => l.replace(/\s/g, '').toUpperCase().includes(pc));
  return [...lines, has ? null : shop.postcode].filter(Boolean).join(', ');
}

export async function api(path, body) {
  const res = await fetch(path, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(data.message ?? 'Something went wrong. Please try again.'), { problems: data.problems, status: res.status });
  return data;
}

// Browser storage can be missing (private windows); the basket then lives only in this page.
export const store = {
  get(key, fallback) { try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* not kept */ } },
};

/** The basket and the customer's details, kept between the menu and checkout pages. */
export const state = {
  basket: store.get('pos.basket', []),
  checkout: store.get('pos.checkout', { type: null, name: '', phone: '', address: '', town: '', postcode: '', notes: '' }),
  save() { store.set('pos.basket', this.basket); store.set('pos.checkout', this.checkout); },
};

/** Collection or delivery: the customer's last choice if the shop still offers it, else delivery first (as most sites do). */
export function orderType(shop) {
  const c = state.checkout;
  if (c.type === 'Delivery' && shop.delivery.enabled) return 'Delivery';
  if (c.type === 'Collection' && shop.collection.enabled) return 'Collection';
  return shop.delivery.enabled ? 'Delivery' : 'Collection';
}

export const findProduct = (menu, id) => menu.products.find(p => p.id === id);
export const findOption = (menu, id) => menu.groups.flatMap(g => g.options).find(o => o.id === id);

/** "Large Doner kebab" and "Chilli sauce, Extra cheese, “no onions”" for a basket or order line. */
export function lineText(menu, line) {
  const p = findProduct(menu, line.productId);
  const v = p?.variants.find(x => x.id === line.variantId);
  const options = (line.optionIds ?? []).map(id => findOption(menu, id)?.name).filter(Boolean);
  return {
    name: [p?.variants.length > 1 ? v?.name : null, p?.name ?? 'Item no longer on the menu'].filter(Boolean).join(' '),
    details: [...options, line.note ? `“${line.note}”` : null].filter(Boolean).join(', '),
  };
}

/** The price of one line (all of its quantity), or null if it can no longer be ordered. */
export function linePence(menu, line) {
  const priced = priceBasket(menu, [line]);
  return priced.problems.length ? null : priced.goodsPence;
}

export function itemCount() { return state.basket.reduce((n, l) => n + l.quantity, 0); }

/** Short-lived message at the bottom of the screen ("Added Garlic bread"). */
export function toast(message) {
  let box = document.querySelector('.toast');
  if (!box) { box = h('div', { class: 'toast', role: 'status' }); document.body.append(box); }
  box.textContent = message;
  box.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => box.classList.remove('show'), 2200);
}

/** Times the shop shows ("about 20 min"). */
export const aboutMinutes = m => (m ? `${m} min` : '');

/** Round shop badge from its name (no picture needed): "Kebab House" → "KH". */
export function initials(name) {
  return String(name ?? '').split(/\s+/).filter(w => /^[A-Za-z0-9]/.test(w)).slice(0, 2).map(w => w[0].toUpperCase()).join('') || '🍽';
}

export { pounds, priceBasket };
