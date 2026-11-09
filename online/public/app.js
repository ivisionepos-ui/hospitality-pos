// The ordering page: menu, item choices, basket, checkout (collection or delivery), pay when you collect or on delivery.
import { postcode as tidyPostcode, pounds, priceBasket, ukPhone } from '/rules.js';

const $ = id => document.getElementById(id);

/** Makes an element: h('p', { class: 'x', text: 'Hi' }, child…). */
function h(tag, attrs = {}, ...children) {
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

async function api(path, body) {
  const res = await fetch(path, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(data.message ?? 'Something went wrong. Please try again.'), { problems: data.problems });
  return data;
}

// Browser storage can be missing (private windows); the basket then lives only in this page.
const store = {
  get(key, fallback) { try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch { return fallback; } },
  set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* not kept */ } },
};

let shop = null;
let menu = { categories: [], products: [], groups: [] };
let basket = store.get('pos.basket', []);
let checkout = store.get('pos.checkout', { type: null, name: '', phone: '', address: '', town: '', postcode: '', notes: '' });

const product = id => menu.products.find(p => p.id === id);
const group = id => menu.groups.find(g => g.id === id);
const fromPrice = p => Math.min(...p.variants.filter(v => !v.soldOut).map(v => v.pricePence));
const save = () => { store.set('pos.basket', basket); store.set('pos.checkout', checkout); };

function lineText(line) {
  const p = product(line.productId);
  const v = p?.variants.find(x => x.id === line.variantId);
  const options = (line.optionIds ?? []).map(id => menu.groups.flatMap(g => g.options).find(o => o.id === id)?.name).filter(Boolean);
  return { name: [p?.variants.length > 1 ? v?.name : null, p?.name ?? 'Item no longer on the menu'].filter(Boolean).join(' '), details: [...options, line.note ? `“${line.note}”` : null].filter(Boolean).join(', ') };
}

// ---------- The page ----------

function renderShop() {
  document.title = shop.configured ? `${shop.name} · Order online` : 'Order online';
  $('shop-name').textContent = shop.configured ? shop.name : 'Order online';
  const status = $('shop-status');
  status.textContent = shop.ordering.open ? 'Open now · taking orders' : shop.ordering.reason;
  status.className = `status ${shop.ordering.open ? 'open' : 'closed'}`;
  if (shop.configured) {
    const ready = [shop.collection.enabled ? `Collection in about ${shop.collection.minutes} min` : null, shop.delivery.enabled ? `Delivery in about ${shop.delivery.minutes} min` : null].filter(Boolean).join(' · ');
    $('shop-info').replaceChildren(...[
      [...(shop.address ?? []), shop.postcode].filter(Boolean).join(', '),
      shop.phone ? h('span', {}, ' · ', h('a', { href: `tel:${shop.phone.replace(/\s/g, '')}`, text: shop.phone })) : null,
      ready ? h('br') : null, ready || null,
    ].filter(x => x != null && x !== ''));
    $('allergen-note').textContent = shop.allergenNote ?? '';
  }
}

function renderMenu() {
  const visible = menu.categories.filter(c => menu.products.some(p => p.categoryId === c.id));
  $('cats').replaceChildren(...visible.map(c => h('a', { href: `#cat-${c.id}`, text: c.name })));
  if (!visible.length) {
    $('menu').replaceChildren(h('p', { text: shop?.configured ? 'The menu is empty right now.' : 'The menu will appear here soon.' }));
    return;
  }

  $('menu').replaceChildren(...visible.map(c => h('section', { id: `cat-${c.id}`, 'aria-labelledby': `h-${c.id}` },
    h('h2', { id: `h-${c.id}`, text: c.name }),
    h('div', { class: 'items' }, menu.products.filter(p => p.categoryId === c.id).map(p => {
      const soldOut = p.soldOut || p.variants.every(v => v.soldOut);
      return h('button', { class: 'item', type: 'button', disabled: soldOut, onclick: () => openItem(p) },
        h('span', { class: 'item-top' }, h('span', { text: p.name }), h('span', { class: 'item-price', text: soldOut ? '' : `${p.variants.length > 1 ? 'from ' : ''}${pounds(fromPrice(p))}` })),
        p.description ? h('span', { class: 'item-desc', text: p.description }) : null,
        p.allergens?.length ? h('span', { class: 'item-allergens', text: `Allergens: ${p.allergens.join(', ')}` }) : null,
        soldOut ? h('span', { class: 'tag', text: 'Sold out' }) : null);
    })))));
}

function renderBasketBar() {
  const priced = priceBasket(menu, basket);
  const count = basket.reduce((n, l) => n + l.quantity, 0);
  $('basket-bar').hidden = count === 0;
  $('basket-open').replaceChildren(h('span', { text: `View basket (${count})` }), h('span', { text: pounds(priced.goodsPence) }));
}

// ---------- Sheets (item, basket, checkout) ----------

const sheet = $('sheet');
const form = $('sheet-form');

function showSheet(title, body, footer) {
  form.replaceChildren(
    h('div', { class: 'sheet-head' }, h('h2', { id: 'sheet-title', text: title }), h('button', { class: 'close', type: 'button', 'aria-label': 'Close', onclick: () => sheet.close(), text: '×' })),
    h('div', { class: 'sheet-body' }, body),
    h('div', { class: 'sheet-foot' }, footer));
  if (!sheet.open) sheet.showModal();
}

function openItem(p) {
  const choice = { variantId: p.variants.find(v => !v.soldOut)?.id, optionIds: [], quantity: 1, note: '' };
  const problem = h('p', { class: 'problem', role: 'alert' });
  const addBtn = h('button', { class: 'btn btn-primary', type: 'button' });
  const price = () => {
    const v = p.variants.find(x => x.id === choice.variantId);
    const extras = choice.optionIds.reduce((s, id) => s + (menu.groups.flatMap(g => g.options).find(o => o.id === id)?.pricePence ?? 0), 0);
    return ((v?.pricePence ?? 0) + extras) * choice.quantity;
  };

  const qty = h('output', { text: '1' });
  const refresh = () => { addBtn.textContent = `Add ${choice.quantity} to basket · ${pounds(price())}`; qty.textContent = String(choice.quantity); };
  const note = h('textarea', { maxlength: 140, placeholder: 'e.g. no onions' });
  note.addEventListener('input', () => { choice.note = note.value; });

  const body = [
    p.description ? h('p', { class: 'item-desc', text: p.description }) : null,
    p.allergens?.length ? h('p', { class: 'item-allergens', text: `Allergens: ${p.allergens.join(', ')}` }) : null,
    p.variants.length > 1 ? h('fieldset', {}, h('legend', { text: 'Size' }), p.variants.map(v => {
      const input = h('input', { type: 'radio', name: 'size', value: v.id, checked: v.id === choice.variantId, disabled: v.soldOut });
      input.addEventListener('change', () => { choice.variantId = v.id; refresh(); });
      return h('label', { class: `choice ${v.soldOut ? 'off' : ''}` }, input, h('span', { text: v.soldOut ? `${v.name} (sold out)` : v.name }), h('span', { text: pounds(v.pricePence) }));
    })) : null,
    p.groupIds.map(group).filter(Boolean).map(g => h('fieldset', {},
      h('legend', {}, g.name, ' ', h('span', { class: 'hint', text: g.min === g.max ? `(choose ${g.min})` : g.min ? `(choose ${g.min} to ${g.max})` : `(optional, up to ${g.max})` })),
      g.options.map(o => {
        const single = g.min === 1 && g.max === 1;
        const input = h('input', { type: single ? 'radio' : 'checkbox', name: `g-${g.id}`, value: o.id, disabled: o.soldOut });
        input.addEventListener('change', () => {
          const mine = g.options.map(x => x.id);
          if (single) choice.optionIds = [...choice.optionIds.filter(id => !mine.includes(id)), o.id];
          else if (input.checked) {
            if (choice.optionIds.filter(id => mine.includes(id)).length >= g.max) { input.checked = false; problem.textContent = `Up to ${g.max} ${g.name.toLowerCase()}.`; return; }
            choice.optionIds.push(o.id);
          } else choice.optionIds = choice.optionIds.filter(id => id !== o.id);
          problem.textContent = '';
          refresh();
        });
        return h('label', { class: `choice ${o.soldOut ? 'off' : ''}` }, input, h('span', { text: o.soldOut ? `${o.name} (sold out)` : o.name }), h('span', { text: o.pricePence ? `+${pounds(o.pricePence)}` : '' }));
      }))),
    h('label', { class: 'field' }, 'Anything we should know?', note),
    h('div', { class: 'qty' }, h('span', { text: 'Quantity' }),
      h('button', { type: 'button', 'aria-label': 'One less', onclick: () => { choice.quantity = Math.max(1, choice.quantity - 1); refresh(); }, text: '−' }), qty,
      h('button', { type: 'button', 'aria-label': 'One more', onclick: () => { choice.quantity = Math.min(50, choice.quantity + 1); refresh(); }, text: '+' })),
    problem,
  ];

  addBtn.addEventListener('click', () => {
    const line = { productId: p.id, variantId: choice.variantId, quantity: choice.quantity, optionIds: choice.optionIds, note: choice.note.trim() || null };
    const check = priceBasket(menu, [line]);
    if (check.problems.length) { problem.textContent = check.problems[0]; return; }
    basket.push({ ...line, key: crypto.randomUUID?.() ?? String(Date.now()) });
    save();
    renderBasketBar();
    sheet.close();
  });
  refresh();
  showSheet(p.name, body, addBtn);
}

function openBasket() {
  const priced = priceBasket(menu, basket);
  const list = h('ul', { class: 'lines' }, basket.map(line => {
    const t = lineText(line);
    return h('li', {},
      h('span', { text: `${line.quantity}×` }),
      h('span', { class: 'what' }, t.name, t.details ? h('small', { text: t.details }) : null),
      h('button', { class: 'btn-link', type: 'button', 'aria-label': `Remove ${t.name}`, onclick: () => { basket = basket.filter(l => l !== line); save(); renderBasketBar(); if (basket.length) openBasket(); else sheet.close(); }, text: 'Remove' }));
  }));
  const go = h('button', { class: 'btn btn-primary', type: 'button', disabled: !shop?.ordering.open || priced.problems.length > 0, onclick: openCheckout, text: `Checkout · ${pounds(priced.goodsPence)}` });
  showSheet('Your basket', [
    list,
    priced.problems.length ? h('p', { class: 'problem', text: `${priced.problems[0]} Remove it to carry on.` }) : null,
    !shop?.ordering.open ? h('p', { class: 'notice', text: shop?.ordering.reason ?? 'We are not taking orders right now.' }) : null,
  ], go);
}

function openCheckout() {
  const priced = priceBasket(menu, basket);
  if (!checkout.type || (checkout.type === 'Delivery' && !shop.delivery.enabled) || (checkout.type === 'Collection' && !shop.collection.enabled)) {
    checkout.type = shop.collection.enabled ? 'Collection' : 'Delivery';
  }

  const problem = h('p', { class: 'problem', role: 'alert' });
  const deliveryNote = h('p', { class: 'hint', role: 'status' });
  let quote = null;
  const input = (label, key, attrs = {}) => {
    const el = h(attrs.multiline ? 'textarea' : 'input', { type: attrs.type ?? 'text', autocomplete: attrs.autocomplete, maxlength: attrs.max ?? 120, inputmode: attrs.inputmode });
    el.value = checkout[key] ?? '';
    el.addEventListener('input', () => { checkout[key] = el.value; save(); if (key === 'postcode') { quote = null; deliveryNote.textContent = ''; refresh(); } });
    return h('label', { class: 'field' }, label, el);
  };

  const totals = h('div', { class: 'totals' });
  const place = h('button', { class: 'btn btn-primary', type: 'button' });
  const refresh = () => {
    const fee = checkout.type === 'Delivery' ? (quote?.status === 'Ok' ? quote.feePence : null) : 0;
    totals.replaceChildren(...[
      h('span', { text: 'Food and drink' }), h('span', { text: pounds(priced.goodsPence) }),
      ...(checkout.type === 'Delivery' ? [h('span', { text: 'Delivery' }), h('span', { text: fee == null ? 'check postcode' : fee === 0 ? 'Free' : pounds(fee) })] : []),
      h('span', { class: 'grand', text: `To pay on ${checkout.type === 'Delivery' ? 'delivery' : 'collection'}` }), h('span', { class: 'grand', text: pounds(priced.goodsPence + (fee ?? 0)) }),
    ]);
    place.textContent = `Place order · ${pounds(priced.goodsPence + (fee ?? 0))}`;
  };

  const checkPostcode = async () => {
    const code = tidyPostcode(checkout.postcode);
    if (!code) { deliveryNote.textContent = 'Enter your full postcode, e.g. B12 9QX.'; return false; }
    deliveryNote.textContent = 'Checking…';
    try {
      quote = await api('/api/delivery-check', { postcode: code, goodsPence: priced.goodsPence });
      checkout.postcode = quote.postcode;
      save();
      deliveryNote.textContent = quote.status === 'Ok' ? `${quote.postcode}: ${quote.miles} miles, delivery ${quote.free ? 'free' : pounds(quote.feePence)}.`
        : quote.status === 'TooFar' ? `Sorry, ${quote.postcode} is too far for us to deliver.`
          : `The minimum order for delivery to ${quote.postcode} is ${pounds(quote.minimumOrderPence)}.`;
      deliveryNote.className = quote.status === 'Ok' ? 'ok-note' : 'problem';
    } catch (e) {
      quote = null;
      deliveryNote.textContent = e.message;
      deliveryNote.className = 'problem';
    }

    refresh();
    return quote?.status === 'Ok';
  };

  const segBtn = (type, label, enabled) => h('button', {
    type: 'button', 'aria-pressed': String(checkout.type === type), disabled: !enabled,
    onclick: () => { checkout.type = type; save(); openCheckout(); }, text: enabled ? label : `${label} (not available)`,
  });

  place.addEventListener('click', async () => {
    problem.textContent = '';
    if (checkout.name.trim().length < 2) { problem.textContent = 'Enter your name.'; return; }
    if (!ukPhone(checkout.phone)) { problem.textContent = 'Enter a UK phone number, so we can call you about your order.'; return; }
    if (checkout.type === 'Delivery') {
      if (checkout.address.trim().length < 3) { problem.textContent = 'Enter your house number and street.'; return; }
      if (!quote && !(await checkPostcode())) { problem.textContent = deliveryNote.textContent; return; }
      if (quote?.status !== 'Ok') { problem.textContent = deliveryNote.textContent; return; }
    }

    place.disabled = true;
    place.textContent = 'Sending your order…';
    try {
      const done = await api('/api/orders', {
        type: checkout.type,
        customer: { name: checkout.name, phone: checkout.phone, notes: checkout.notes, address: checkout.address, town: checkout.town, postcode: checkout.postcode },
        lines: basket.map(({ productId, variantId, quantity, optionIds, note }) => ({ productId, variantId, quantity, optionIds, note })),
      });
      basket = [];
      save();
      store.set('pos.lastOrder', { id: done.id, token: done.token });
      location.href = `/order?id=${encodeURIComponent(done.id)}&t=${encodeURIComponent(done.token)}`;
    } catch (e) {
      problem.textContent = e.message;
      place.disabled = false;
      refresh();
    }
  });

  const postcodeField = input('Postcode', 'postcode', { autocomplete: 'postal-code', max: 10 });
  postcodeField.querySelector('input').addEventListener('change', checkPostcode);
  showSheet('Checkout', [
    h('div', { class: 'seg', role: 'group', 'aria-label': 'Collection or delivery' },
      segBtn('Collection', `Collection (${shop.collection.minutes} min)`, shop.collection.enabled),
      segBtn('Delivery', `Delivery (${shop.delivery.minutes ?? ''} min)`, shop.delivery.enabled)),
    input('Your name', 'name', { autocomplete: 'name', max: 60 }),
    input('Phone number', 'phone', { type: 'tel', autocomplete: 'tel', max: 20, inputmode: 'tel' }),
    checkout.type === 'Delivery' ? [
      input('House number and street', 'address', { autocomplete: 'street-address' }),
      input('Town (optional)', 'town', { autocomplete: 'address-level2', max: 60 }),
      postcodeField, deliveryNote,
    ] : null,
    input(checkout.type === 'Delivery' ? 'Notes for the driver (optional)' : 'Notes (optional)', 'notes', { multiline: true, max: 200 }),
    totals,
    h('p', { class: 'hint', text: `You pay when you ${checkout.type === 'Delivery' ? 'receive your order' : 'collect'}: cash or card. We use your name and number only for this order (see Privacy).` }),
    problem,
  ], place);
  refresh();
  if (checkout.type === 'Delivery' && tidyPostcode(checkout.postcode)) checkPostcode();
}

$('basket-open').addEventListener('click', openBasket);
form.addEventListener('submit', e => e.preventDefault()); // Enter in a box must not close the sheet

// ---------- Start ----------

async function load() {
  try {
    [shop, menu] = await Promise.all([api('/api/shop'), api('/api/menu')]);
  } catch {
    $('shop-status').textContent = 'The menu could not be loaded. Please refresh the page.';
    return;
  }

  // Items that left the menu since the basket was saved are dropped quietly.
  basket = basket.filter(l => product(l.productId));
  renderShop();
  renderMenu();
  renderBasketBar();
  const last = store.get('pos.lastOrder', null);
  if (last?.id) $('shop-info').append(h('br'), h('a', { href: `/order?id=${encodeURIComponent(last.id)}&t=${encodeURIComponent(last.token)}`, text: 'Your last order' }));
}

load();
setInterval(async () => { try { shop = await api('/api/shop'); renderShop(); } catch { /* next time */ } }, 60000);
