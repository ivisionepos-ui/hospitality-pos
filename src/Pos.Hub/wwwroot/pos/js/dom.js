// Small helpers for building the screen. Text always goes in as text (never as HTML),
// so names typed into the menu can never inject code into the page.

/**
 * Creates an element. attrs: class, text, on* handlers (onclick…), dataset (data), style, disabled, other attributes.
 * Children: elements, strings (added as text), arrays, or null/false (skipped).
 */
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key === 'text') el.textContent = value;
    else if (key === 'data') Object.assign(el.dataset, value);
    else if (key === 'style') for (const [name, v] of Object.entries(value)) el.style.setProperty(name, v);
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (value === true) el.setAttribute(key, '');
    else el.setAttribute(key, String(value));
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    if (Array.isArray(child)) append(el, child);
    else if (child instanceof Node) el.appendChild(child);
    else el.appendChild(document.createTextNode(String(child)));
  }
}

/** Replaces everything inside an element. */
export function mount(el, ...children) {
  el.replaceChildren();
  append(el, children);
  return el;
}

/** A Money from the hub ({ minorUnits, currency }) as "£12.50" / "-£2.00". */
export function money(value) {
  if (value === null || value === undefined) return '';
  const pence = typeof value === 'number' ? value : value.minorUnits;
  return penceText(pence);
}

/** Pence as "£12.50". */
export function penceText(pence) {
  const sign = pence < 0 ? '-' : '';
  const abs = Math.abs(pence);
  return `${sign}£${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, '0')}`;
}

/** Pence from a Money. */
export function pence(value) {
  return value === null || value === undefined ? 0 : (typeof value === 'number' ? value : value.minorUnits);
}

/** Text for a localized name ({ en, ar }). English only for now. */
export function text(localized) {
  return localized ? localized.en : '';
}

/** A new random id (UUID v4, or v7-like when available). */
export function newId() {
  if (globalThis.crypto?.randomUUID) return crypto.randomUUID();
  // Fallback for plain-HTTP pages on older browsers.
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const x = [...b].map(v => v.toString(16).padStart(2, '0')).join('');
  return `${x.slice(0, 8)}-${x.slice(8, 12)}-${x.slice(12, 16)}-${x.slice(16, 20)}-${x.slice(20)}`;
}

/** Shows a short message at the bottom of the screen. kind: info | ok | error. */
export function toast(message, kind = 'info', ms = 3500) {
  const host = document.getElementById('toasts');
  const el = h('div', { class: `toast toast-${kind}`, role: kind === 'error' ? 'alert' : 'status', text: message });
  host.appendChild(el);
  // Keep at most two on screen, so they never pile up over the work.
  while (host.children.length > 2) host.firstElementChild.remove();
  setTimeout(() => el.classList.add('toast-hide'), ms);
  setTimeout(() => el.remove(), ms + 400);
}

/** localStorage that never throws (private windows, blocked storage). */
export const store = {
  get(key) {
    try { const v = localStorage.getItem(key); return v ? JSON.parse(v) : null; } catch { return null; }
  },
  set(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* storage unavailable */ }
  },
  remove(key) {
    try { localStorage.removeItem(key); } catch { /* storage unavailable */ }
  },
};

/** Time as "18:42" in the browser's (shop's) time zone. */
export function timeText(iso) {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
