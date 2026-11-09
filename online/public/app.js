// The menu page, laid out the way customers know from the big takeaway apps: the shop at the top, delivery or
// collection, search, menu sections with a "+" on every item, choices in a pop-up, and the order always in view
// (on the right on a computer, a "View basket" bar on a phone). Checkout is its own page (/checkout).
import { $, aboutMinutes, addressLine, put, api, findOption, h, initials, itemCount, lineText, linePence, orderType, pounds, priceBasket, state, store, toast } from '/common.js';

let shop = null;
let menu = { categories: [], products: [], groups: [] };

const group = id => menu.groups.find(g => g.id === id);
const fromPrice = p => Math.min(...p.variants.filter(v => !v.soldOut).map(v => v.pricePence));
const isSoldOut = p => p.soldOut || p.variants.every(v => v.soldOut);
const isSimple = p => p.variants.length === 1 && p.groupIds.map(group).filter(Boolean).length === 0;

// ---------- Shop header ----------

function renderShop() {
  const name = shop.configured ? shop.name : 'Order online';
  document.title = shop.configured ? `${shop.name} · Order online` : 'Order online';
  $('brand').textContent = name;
  $('shop-name').textContent = name;
  $('logo').textContent = initials(name);
  $('shop-address').textContent = shop.configured ? addressLine(shop) : '';
  const status = $('shop-status');
  status.textContent = shop.ordering.open ? 'Open now · taking orders' : shop.ordering.reason;
  status.className = `status ${shop.ordering.open ? 'open' : 'closed'}`;
  if (!shop.configured) return;

  const facts = [];
  if (shop.delivery.enabled) facts.push(['🛵', 'Delivery', `about ${aboutMinutes(shop.delivery.minutes)}`]);
  if (shop.collection.enabled) facts.push(['🛍️', 'Collection', `about ${aboutMinutes(shop.collection.minutes)}`]);
  if (shop.delivery.enabled && shop.delivery.minimumOrderPence) facts.push(['🧾', 'Min. order for delivery', pounds(shop.delivery.minimumOrderPence)]);
  facts.push(['💷', 'Pay on ' + (shop.delivery.enabled && shop.collection.enabled ? 'delivery or collection' : shop.delivery.enabled ? 'delivery' : 'collection'), 'cash or card']);
  if (shop.phone) facts.push(['📞', 'Call us', h('a', { href: `tel:${shop.phone.replace(/\s/g, '')}`, text: shop.phone })]);
  $('facts').replaceChildren(...facts.map(([icon, label, value]) => h('li', {},
    h('span', { class: 'fact-icon', 'aria-hidden': 'true', text: icon }), h('span', {}, h('strong', { text: label }), h('span', {}, value)))));
  $('allergen-note').textContent = shop.allergenNote ?? '';
}

// ---------- Menu ----------

let observer = null;

function renderMenu() {
  const words = $('search').value.trim().toLowerCase();
  const matches = p => !words || `${p.name} ${p.description ?? ''}`.toLowerCase().includes(words);
  const visible = menu.categories
    .map(c => ({ c, items: menu.products.filter(p => p.categoryId === c.id && matches(p)) }))
    .filter(x => x.items.length);

  $('cats').replaceChildren(...visible.map(({ c }) => h('a', { href: `#cat-${c.id}`, 'data-cat': c.id, text: c.name })));
  if (!visible.length) {
    $('menu').replaceChildren(h('p', { class: 'empty', text: words ? `Nothing on the menu matches “${$('search').value.trim()}”.` : shop?.configured ? 'The menu is empty right now.' : 'The menu will appear here soon.' }));
    return;
  }

  $('menu').replaceChildren(...visible.map(({ c, items }) => h('section', { id: `cat-${c.id}`, class: 'cat', 'aria-labelledby': `h-${c.id}` },
    h('h2', { id: `h-${c.id}`, text: c.name }),
    h('ul', { class: 'items' }, items.map(itemRow)))));
  watchSections();
}

function itemRow(p) {
  const soldOut = isSoldOut(p);
  const price = soldOut ? 'Sold out' : `${p.variants.length > 1 ? 'from ' : ''}${pounds(fromPrice(p))}`;
  return h('li', { class: `item ${soldOut ? 'off' : ''}` },
    h('button', { class: 'item-main', type: 'button', disabled: soldOut, 'aria-label': `${p.name} ${price}`, onclick: () => openItem(p) },
      h('span', { class: 'item-name', text: p.name }),
      p.description ? h('span', { class: 'item-desc', text: p.description }) : null,
      h('span', { class: 'item-price', text: price }),
      p.allergens?.length ? h('span', { class: 'item-allergens', text: `Allergens: ${p.allergens.join(', ')}` }) : null),
    soldOut ? null : h('button', {
      class: 'add', type: 'button', 'aria-label': `Add ${p.name}`,
      onclick: () => (isSimple(p) ? addLine({ productId: p.id, variantId: p.variants[0].id, quantity: 1, optionIds: [], note: null }, p.name) : openItem(p)),
      text: '+',
    }));
}

/** Highlights the section being looked at in the sticky bar, like the big apps do. */
function watchSections() {
  observer?.disconnect();
  if (!('IntersectionObserver' in window)) return;
  observer = new IntersectionObserver(entries => {
    const seen = entries.filter(e => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
    if (!seen) return;
    const id = seen.target.id.slice(4);
    for (const a of $('cats').querySelectorAll('a')) {
      const on = a.dataset.cat === id;
      a.classList.toggle('on', on);
      if (on) a.scrollIntoView({ block: 'nearest', inline: 'center' });
    }
  }, { rootMargin: '-120px 0px -60% 0px' });
  for (const s of document.querySelectorAll('.cat')) observer.observe(s);
}

// ---------- Item pop-up ----------

const sheet = $('sheet');
const form = $('sheet-form');

function openItem(p) {
  const choice = { variantId: p.variants.length === 1 ? p.variants[0].id : null, optionIds: [], quantity: 1, note: '' };
  const groups = p.groupIds.map(group).filter(Boolean);
  const addBtn = h('button', { class: 'btn btn-primary add-btn', type: 'button' });
  const qty = h('output', { 'aria-live': 'polite', text: '1' });
  const sections = new Map(); // which fieldset to point at when a required choice is missing

  const unit = () => (p.variants.find(x => x.id === choice.variantId)?.pricePence ?? fromPrice(p))
    + choice.optionIds.reduce((s, id) => s + (findOption(menu, id)?.pricePence ?? 0), 0);
  const refresh = () => {
    addBtn.textContent = `Add to order · ${pounds(unit() * choice.quantity)}`;
    qty.textContent = String(choice.quantity);
  };

  const badge = required => h('span', { class: `badge ${required ? 'req' : ''}`, text: required ? 'Required' : 'Optional' });
  const section = (key, title, hint, required, children) => {
    const fs = h('fieldset', { class: 'choice-group' },
      h('legend', {}, h('span', { class: 'cg-title', text: title }), badge(required)),
      h('p', { class: 'cg-hint', text: hint }), children);
    sections.set(key, fs);
    return fs;
  };

  const sizes = p.variants.length > 1 ? section('size', 'Choose your size', 'Choose 1', true, p.variants.map(v => {
    const input = h('input', { type: 'radio', name: 'size', value: v.id, disabled: v.soldOut });
    input.addEventListener('change', () => { choice.variantId = v.id; sections.get('size').classList.remove('missing'); refresh(); });
    return h('label', { class: `choice ${v.soldOut ? 'off' : ''}` }, input, h('span', { class: 'choice-name', text: v.soldOut ? `${v.name} (sold out)` : v.name }), h('span', { class: 'choice-price', text: pounds(v.pricePence) }));
  })) : null;

  const extras = groups.map(g => {
    const single = g.min === 1 && g.max === 1;
    const hint = g.min === g.max ? `Choose ${g.min}` : g.min ? `Choose ${g.min} to ${g.max}` : `Choose up to ${g.max}`;
    return section(g.id, g.name, hint, g.min > 0, g.options.map(o => {
      const input = h('input', { type: single ? 'radio' : 'checkbox', name: `g-${g.id}`, value: o.id, disabled: o.soldOut });
      input.addEventListener('change', () => {
        const mine = g.options.map(x => x.id);
        if (single) choice.optionIds = [...choice.optionIds.filter(id => !mine.includes(id)), o.id];
        else if (input.checked) {
          if (choice.optionIds.filter(id => mine.includes(id)).length >= g.max) { input.checked = false; toast(`Up to ${g.max} ${g.name.toLowerCase()}.`); return; }
          choice.optionIds.push(o.id);
        } else choice.optionIds = choice.optionIds.filter(id => id !== o.id);
        sections.get(g.id).classList.remove('missing');
        refresh();
      });
      return h('label', { class: `choice ${o.soldOut ? 'off' : ''}` }, input, h('span', { class: 'choice-name', text: o.soldOut ? `${o.name} (sold out)` : o.name }), h('span', { class: 'choice-price', text: o.pricePence ? `+${pounds(o.pricePence)}` : '' }));
    }));
  });

  const note = h('textarea', { maxlength: 140, placeholder: 'e.g. no onions, well done', id: 'item-note' });
  note.addEventListener('input', () => { choice.note = note.value; });

  addBtn.addEventListener('click', () => {
    // A required choice still missing: point at it, as the big apps do.
    const missing = (!choice.variantId && 'size') || groups.find(g => choice.optionIds.filter(id => g.options.some(o => o.id === id)).length < g.min)?.id;
    if (missing) {
      const fs = sections.get(missing);
      fs.classList.add('missing');
      fs.scrollIntoView({ behavior: 'smooth', block: 'center' });
      return;
    }

    const line = { productId: p.id, variantId: choice.variantId, quantity: choice.quantity, optionIds: choice.optionIds, note: choice.note.trim() || null };
    const check = priceBasket(menu, [line]);
    if (check.problems.length) { toast(check.problems[0]); return; }
    sheet.close();
    addLine(line, p.name);
  });

  refresh();
  form.replaceChildren(
    h('div', { class: 'sheet-head' },
      h('div', {}, h('h2', { id: 'sheet-title', text: p.name }),
        h('p', { class: 'sheet-price', text: `${p.variants.length > 1 ? 'from ' : ''}${pounds(fromPrice(p))}` })),
      h('button', { class: 'close', type: 'button', 'aria-label': 'Close', onclick: () => sheet.close(), text: '×' })),
    h('div', { class: 'sheet-body' },
      p.description ? h('p', { class: 'item-desc', text: p.description }) : null,
      p.allergens?.length ? h('p', { class: 'item-allergens', text: `Allergens: ${p.allergens.join(', ')}` }) : null,
      sizes, extras,
      h('div', { class: 'choice-group' }, h('label', { class: 'cg-title', for: 'item-note', text: 'Special instructions' }), note)),
    h('div', { class: 'sheet-foot' },
      h('div', { class: 'stepper' },
        h('button', { type: 'button', 'aria-label': 'One less', onclick: () => { choice.quantity = Math.max(1, choice.quantity - 1); refresh(); }, text: '−' }), qty,
        h('button', { type: 'button', 'aria-label': 'One more', onclick: () => { choice.quantity = Math.min(50, choice.quantity + 1); refresh(); }, text: '+' })),
      addBtn));
  sheet.showModal();
  form.querySelector('.sheet-body').scrollTop = 0;
}

function addLine(line, name) {
  // The same thing with the same choices just adds to the quantity.
  const same = state.basket.find(l => l.productId === line.productId && l.variantId === line.variantId && !l.note && !line.note
    && [...l.optionIds].sort().join() === [...line.optionIds].sort().join());
  if (same) same.quantity = Math.min(50, same.quantity + line.quantity);
  else state.basket.push({ ...line, key: crypto.randomUUID?.() ?? String(Date.now()) });
  state.save();
  renderBasket();
  toast(`Added ${line.quantity > 1 ? `${line.quantity} × ` : ''}${name}`);
}

// ---------- Basket (side panel on a computer, full-screen on a phone) ----------

function basketContent() {
  const type = orderType(shop);
  const priced = priceBasket(menu, state.basket);
  const minimum = type === 'Delivery' ? shop.delivery.minimumOrderPence : null;
  const short = minimum ? Math.max(0, minimum - priced.goodsPence) : 0;

  const switcher = h('div', { class: 'seg', role: 'group', 'aria-label': 'Delivery or collection' },
    ['Delivery', 'Collection'].map(t => {
      const offer = t === 'Delivery' ? shop.delivery : shop.collection;
      return h('button', {
        type: 'button', 'aria-pressed': String(type === t), disabled: !offer.enabled,
        onclick: () => { state.checkout.type = t; state.save(); renderBasket(); },
      }, h('strong', { text: t }), h('small', { text: offer.enabled ? aboutMinutes(offer.minutes) : 'not available' }));
    }));

  if (!state.basket.length) {
    return [h('h2', { class: 'basket-title', text: 'Your order' }), switcher,
      h('div', { class: 'basket-empty' }, h('span', { 'aria-hidden': 'true', text: '🛒' }), h('p', { text: 'Your basket is empty' }), h('p', { class: 'muted', text: 'Add something tasty from the menu.' }))];
  }

  const lines = h('ul', { class: 'lines' }, state.basket.map(line => {
    const t = lineText(menu, line);
    const pence = linePence(menu, line);
    const change = by => {
      line.quantity += by;
      if (line.quantity < 1) state.basket = state.basket.filter(l => l !== line);
      line.quantity = Math.min(50, line.quantity);
      state.save();
      renderBasket();
    };
    return h('li', {},
      h('div', { class: 'line-top' }, h('span', { class: 'line-name', text: t.name }), h('span', { class: 'line-price', text: pence == null ? '—' : pounds(pence) })),
      t.details ? h('small', { class: 'line-details', text: t.details }) : null,
      pence == null ? h('small', { class: 'problem', text: 'No longer available. Remove it to carry on.' }) : null,
      h('div', { class: 'stepper small' },
        h('button', { type: 'button', 'aria-label': line.quantity === 1 ? `Remove ${t.name}` : `One less ${t.name}`, onclick: () => change(-1), text: line.quantity === 1 ? '🗑' : '−' }),
        h('output', { text: String(line.quantity) }),
        h('button', { type: 'button', 'aria-label': `One more ${t.name}`, onclick: () => change(1), text: '+' })));
  }));

  const blocked = !shop.ordering.open || priced.problems.length > 0 || short > 0;
  return [
    h('h2', { class: 'basket-title', text: 'Your order' }), switcher, lines,
    h('dl', { class: 'totals' },
      h('dt', { text: 'Subtotal' }), h('dd', { text: pounds(priced.goodsPence) }),
      type === 'Delivery' ? [h('dt', { text: 'Delivery fee' }), h('dd', { class: 'muted', text: 'from your postcode' })] : null),
    short > 0 ? h('div', { class: 'minimum' },
      h('p', { text: `Spend ${pounds(short)} more for delivery (minimum order ${pounds(minimum)}).` }),
      h('div', { class: 'bar', role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': String(minimum), 'aria-valuenow': String(priced.goodsPence) },
        progress(priced.goodsPence / minimum))) : null,
    !shop.ordering.open ? h('p', { class: 'notice', text: shop.ordering.reason }) : null,
    h('a', {
      class: `btn btn-primary checkout-btn ${blocked ? 'disabled' : ''}`, href: blocked ? null : '/checkout', role: 'button', 'aria-disabled': String(blocked),
    }, h('span', { text: 'Go to checkout' }), h('span', { text: pounds(priced.goodsPence) })),
  ];
}

/** The filled part of a progress bar (set through the style property: the page's security rules block style="…"). */
function progress(fraction) {
  const fill = h('span');
  fill.style.width = `${Math.min(100, Math.round(fraction * 100))}%`;
  return fill;
}

function renderBasket() {
  if (!shop) return;
  put($('basket-panel'), basketContent());
  if ($('basket-sheet').open) {
    put($('basket-sheet-body'),
      h('div', { class: 'sheet-head' }, h('h2', { text: 'Your order' }), h('button', { class: 'close', type: 'button', 'aria-label': 'Close', onclick: () => $('basket-sheet').close(), text: '×' })),
      h('div', { class: 'sheet-body basket-panel in-sheet' }, basketContent().slice(1)));
  }

  const count = itemCount();
  const total = priceBasket(menu, state.basket).goodsPence;
  $('top-count').textContent = String(count);
  $('top-basket').setAttribute('aria-label', `Basket, ${count} ${count === 1 ? 'item' : 'items'}`);
  $('basket-bar').hidden = count === 0;
  $('basket-open').replaceChildren(h('span', { class: 'bubble', text: String(count) }), h('span', { text: 'View basket' }), h('span', { text: pounds(total) }));
}

function openBasketSheet() {
  if (!shop) return;
  if (!$('basket-sheet').open) $('basket-sheet').showModal();
  renderBasket();
}

$('basket-open').addEventListener('click', openBasketSheet);
$('top-basket').addEventListener('click', () => {
  if (window.matchMedia('(min-width: 1024px)').matches) $('basket-panel').scrollIntoView({ behavior: 'smooth' });
  else openBasketSheet();
});
form.addEventListener('submit', e => e.preventDefault()); // Enter in a box must not close the pop-up
for (const d of [sheet, $('basket-sheet')]) d.addEventListener('click', e => { if (e.target === d) d.close(); }); // tap outside closes
$('search').addEventListener('input', renderMenu);

// ---------- Start ----------

async function load() {
  try {
    [shop, menu] = await Promise.all([api('/api/shop'), api('/api/menu')]);
  } catch {
    $('shop-status').textContent = 'The menu could not be loaded. Please refresh the page.';
    return;
  }

  // Items that left the menu since the basket was saved are dropped quietly.
  state.basket = state.basket.filter(l => menu.products.some(p => p.id === l.productId));
  renderShop();
  renderMenu();
  renderBasket();
  const last = store.get('pos.lastOrder', null);
  if (last?.id) {
    $('last-order').replaceChildren(h('a', { class: 'last-order', href: `/order?id=${encodeURIComponent(last.id)}&t=${encodeURIComponent(last.token)}` }, 'Follow your last order →'));
  }
}

load();
setInterval(async () => { try { shop = await api('/api/shop'); renderShop(); renderBasket(); } catch { /* next time */ } }, 60000);
