// Checkout: delivery or collection, your details, address (delivery), when, a note, how you pay, then Place my order.
// The order summary stays beside the form on a computer and below it on a phone.
import { postcode as tidyPostcode, ukPhone } from '/rules.js';
import { $, aboutMinutes, put, api, h, lineText, linePence, orderType, pounds, priceBasket, state, store } from '/common.js';

let shop = null;
let menu = null;
let quote = null;         // the last delivery check for the postcode typed
let quotedFor = null;     // …and the postcode and basket value it was for

const c = state.checkout;
const errors = new Map(); // field → message shown under it

function field(label, key, attrs = {}) {
  const id = `f-${key}`;
  const input = h(attrs.multiline ? 'textarea' : 'input', {
    id, name: key, type: attrs.type ?? 'text', autocomplete: attrs.autocomplete, maxlength: attrs.max ?? 120, inputmode: attrs.inputmode,
    placeholder: attrs.placeholder, 'aria-describedby': `${id}-msg`, 'aria-invalid': errors.has(key) ? 'true' : null,
  });
  input.value = c[key] ?? '';
  input.addEventListener('input', () => {
    c[key] = input.value;
    state.save();
    if (errors.delete(key)) { input.removeAttribute('aria-invalid'); input.parentElement.classList.remove('bad'); $(`${id}-msg`).textContent = attrs.hint ?? ''; }
  });
  if (key === 'postcode') input.addEventListener('change', () => checkPostcode().then(render));
  return h('div', { class: `field ${errors.has(key) ? 'bad' : ''}` },
    h('label', { for: id, text: label }), input,
    h('p', { id: `${id}-msg`, class: 'field-msg', role: errors.has(key) ? 'alert' : null, text: errors.get(key) ?? attrs.hint ?? '' }));
}

const card = (title, ...body) => h('section', { class: 'card' }, h('h2', { text: title }), body);

async function checkPostcode() {
  const goods = priceBasket(menu, state.basket).goodsPence;
  const code = tidyPostcode(c.postcode);
  if (!code) { quote = null; return false; }
  if (quote && quotedFor === `${code}|${goods}`) return quote.status === 'Ok';
  try {
    quote = await api('/api/delivery-check', { postcode: code, goodsPence: goods });
    quotedFor = `${code}|${goods}`;
    c.postcode = quote.postcode;
    state.save();
    if (quote.status === 'Ok') errors.delete('postcode');
    else errors.set('postcode', quote.status === 'TooFar' ? `Sorry, ${quote.postcode} is too far for us to deliver.` : `The minimum order for delivery to ${quote.postcode} is ${pounds(quote.minimumOrderPence)}.`);
  } catch (e) {
    quote = null;
    errors.set('postcode', e.message);
  }

  return quote?.status === 'Ok';
}

function render() {
  const type = orderType(shop);
  c.type = type;
  const priced = priceBasket(menu, state.basket);
  const fee = type === 'Delivery' ? (quote?.status === 'Ok' && tidyPostcode(c.postcode) === quote.postcode ? quote.feePence : null) : 0;
  const total = priced.goodsPence + (fee ?? 0);
  const minutes = type === 'Delivery' ? shop.delivery.minutes : shop.collection.minutes;

  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Delivery or collection' }, ['Delivery', 'Collection'].map(t => {
    const offer = t === 'Delivery' ? shop.delivery : shop.collection;
    return h('button', { type: 'button', 'aria-pressed': String(type === t), disabled: !offer.enabled, onclick: () => { c.type = t; state.save(); render(); } },
      h('strong', { text: t }), h('small', { text: offer.enabled ? aboutMinutes(offer.minutes) : 'not available' }));
  }));

  const place = h('button', { class: 'btn btn-primary place-btn', type: 'submit', disabled: !shop.ordering.open || priced.problems.length > 0 },
    h('span', { text: 'Place my order' }), h('span', { text: pounds(total) }));

  put($('checkout'),
    card('Delivery or collection', seg),
    card('Your details',
      field('Full name', 'name', { autocomplete: 'name', max: 60 }),
      field('Mobile number', 'phone', { type: 'tel', autocomplete: 'tel', max: 20, inputmode: 'tel', hint: 'So we can call you about your order.' })),
    type === 'Delivery' ? card('Delivery address',
      field('House number and street', 'address', { autocomplete: 'street-address' }),
      field('Town (optional)', 'town', { autocomplete: 'address-level2', max: 60 }),
      field('Postcode', 'postcode', { autocomplete: 'postal-code', max: 10, placeholder: 'e.g. B12 9QX',
        hint: quote?.status === 'Ok' && tidyPostcode(c.postcode) === quote.postcode ? `✓ We deliver to ${quote.postcode} (${quote.miles} miles).` : '' })) : null,
    card(type === 'Delivery' ? 'Delivery time' : 'Collection time',
      h('div', { class: 'option-card on' }, h('span', { class: 'dot', 'aria-hidden': 'true' }),
        h('span', {}, h('strong', { text: 'As soon as possible' }), h('small', { text: `About ${minutes} min after the shop accepts your order` })))),
    card(type === 'Delivery' ? 'Note for the shop and driver' : 'Note for the shop',
      field('Anything we should know? (optional)', 'notes', { multiline: true, max: 200, placeholder: type === 'Delivery' ? 'e.g. ring the bell, flat 2 is upstairs' : 'e.g. I will be there at 7' })),
    card('Payment',
      h('div', { class: 'option-card on' }, h('span', { class: 'dot', 'aria-hidden': 'true' }),
        h('span', {}, h('strong', { text: `Pay on ${type === 'Delivery' ? 'delivery' : 'collection'}` }), h('small', { text: 'Cash or card. Nothing to pay now.' })))),
    !shop.ordering.open ? h('p', { class: 'notice', text: shop.ordering.reason }) : null,
    h('p', { id: 'form-problem', class: 'problem', role: 'alert', text: errors.get('form') ?? '' }),
    h('div', { class: 'place-wrap' }, place,
      h('p', { class: 'muted small', }, 'We use your name, number and address only for this order. ', h('a', { href: '/privacy', text: 'Privacy' }))));

  put($('summary'),
    h('div', { class: 'summary-head' }, h('h2', { class: 'basket-title', text: 'Your order' }), h('a', { href: '/', text: 'Edit' })),
    h('ul', { class: 'lines' }, state.basket.map(line => {
      const t = lineText(menu, line);
      const pence = linePence(menu, line);
      return h('li', {},
        h('div', { class: 'line-top' }, h('span', { class: 'line-name', text: `${line.quantity} × ${t.name}` }), h('span', { class: 'line-price', text: pence == null ? '—' : pounds(pence) })),
        t.details ? h('small', { class: 'line-details', text: t.details }) : null);
    })),
    h('dl', { class: 'totals' },
      h('dt', { text: 'Subtotal' }), h('dd', { text: pounds(priced.goodsPence) }),
      type === 'Delivery' ? [h('dt', { text: 'Delivery fee' }), h('dd', { text: fee == null ? 'enter postcode' : fee === 0 ? 'Free' : pounds(fee) })] : null,
      h('dt', { class: 'grand', text: 'Total' }), h('dd', { class: 'grand', text: pounds(total) })),
    priced.problems.length ? h('p', { class: 'problem', text: `${priced.problems[0]} Go back to the menu to change it.` }) : null);
}

async function placeOrder(e) {
  e.preventDefault();
  errors.clear();
  const type = orderType(shop);
  if (c.name.trim().length < 2) errors.set('name', 'Enter your name.');
  if (!ukPhone(c.phone)) errors.set('phone', 'Enter a UK mobile or landline number.');
  if (type === 'Delivery') {
    if (c.address.trim().length < 3) errors.set('address', 'Enter your house number and street.');
    if (!tidyPostcode(c.postcode)) errors.set('postcode', 'Enter your full postcode, e.g. B12 9QX.');
    else await checkPostcode();
  }

  if (errors.size) {
    errors.set('form', 'Please check the details marked in red.');
    render();
    document.querySelector('[aria-invalid="true"]')?.focus();
    return;
  }

  const button = document.querySelector('.place-btn');
  button.disabled = true;
  button.firstChild.textContent = 'Sending your order…';
  try {
    const done = await api('/api/orders', {
      type,
      customer: { name: c.name, phone: c.phone, notes: c.notes, address: c.address, town: c.town, postcode: c.postcode },
      lines: state.basket.map(({ productId, variantId, quantity, optionIds, note }) => ({ productId, variantId, quantity, optionIds, note })),
    });
    state.basket = [];
    state.save();
    store.set('pos.lastOrder', { id: done.id, token: done.token });
    location.href = `/order?id=${encodeURIComponent(done.id)}&t=${encodeURIComponent(done.token)}`;
  } catch (err) {
    errors.set('form', err.message);
    render();
  }
}

$('checkout').addEventListener('submit', placeOrder);

async function load() {
  try {
    [shop, menu] = await Promise.all([api('/api/shop'), api('/api/menu')]);
  } catch {
    $('checkout').replaceChildren(h('p', { class: 'problem', text: 'The page could not be loaded. Please refresh it.' }));
    return;
  }

  if (shop.configured) { $('brand').textContent = shop.name; document.title = `Checkout · ${shop.name}`; }
  state.basket = state.basket.filter(l => menu.products.some(p => p.id === l.productId));
  if (!state.basket.length) {
    $('checkout').replaceChildren(h('section', { class: 'card' }, h('h2', { text: 'Your basket is empty' }), h('p', {}, h('a', { class: 'btn btn-primary', href: '/', text: 'Back to the menu' }))));
    $('summary').hidden = true;
    return;
  }

  render();
  if (orderType(shop) === 'Delivery' && tidyPostcode(c.postcode)) { await checkPostcode(); render(); }
}

load();
