// The customer's order page: the expected time in large type, a progress bar, and what was ordered.
// Checked every 10 seconds until the order is finished.
import { $, h, lineText, pounds, put } from '/common.js';

const params = new URLSearchParams(location.search);
const id = params.get('id');
const token = params.get('t');
const time = iso => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/London' });
let menu = null;
let shownLines = false;

const HEADLINE = {
  Placed: () => 'Sending your order to the shop…',
  Received: o => `The shop has your order (number ${o.number}). Waiting for them to accept it.`,
  Accepted: o => (o.type === 'Delivery' ? `Accepted. It should arrive about ${time(o.readyAt)}.` : `Accepted. It should be ready about ${time(o.readyAt)}.`),
  Ready: o => (o.type === 'Delivery' ? 'Your food is ready and will be on its way soon.' : 'Your order is ready to collect.'),
  OutForDelivery: () => 'Your order is on its way.',
  Completed: () => 'Done. Enjoy your meal!',
  Rejected: o => `Sorry, the shop could not take your order${o.reason ? `: ${o.reason}` : '.'}`,
  Cancelled: o => `Your order was cancelled${o.reason ? `: ${o.reason}` : '.'}`,
  NotReceived: () => 'The shop did not receive your order. Please phone them.',
};

function render(o) {
  $('title').textContent = o.number ? `Order ${o.number} · ${o.type === 'Delivery' ? 'Delivery' : 'Collection'}` : 'Your order';
  $('headline').textContent = (HEADLINE[o.status] ?? (() => o.status))(o);
  const showEta = o.readyAt && ['Accepted', 'Ready', 'OutForDelivery'].includes(o.status);
  $('eta').hidden = !showEta;
  if (showEta) $('eta').replaceChildren(h('small', { text: o.type === 'Delivery' ? 'Estimated arrival' : 'Ready to collect at' }), time(o.readyAt));

  const failed = ['Rejected', 'Cancelled', 'NotReceived'].includes(o.status);
  $('headline').classList.toggle('problem', failed);
  const flow = o.type === 'Delivery' ? ['Received', 'Accepted', 'Ready', 'OutForDelivery', 'Completed'] : ['Received', 'Accepted', 'Ready', 'Completed'];
  const labels = { Received: 'Sent', Accepted: 'Accepted', Ready: 'Ready', OutForDelivery: 'On its way', Completed: o.type === 'Delivery' ? 'Delivered' : 'Collected' };
  const at = flow.indexOf(o.status);
  put($('steps'), (failed ? [] : flow.map((s, i) => h('li', {
    class: i < at || o.status === 'Completed' ? 'done' : i === at ? 'now' : null, 'aria-current': i === at ? 'step' : null,
  }, h('span', { class: 'pip', 'aria-hidden': 'true' }), h('span', { text: labels[s] })))));

  if (!shownLines) {
    shownLines = true;
    $('details-card').hidden = false;
    put($('details'),
      menu ? h('ul', { class: 'lines' }, (o.lines ?? []).map(l => {
        const t = lineText(menu, l);
        return h('li', {}, h('div', { class: 'line-top' }, h('span', { class: 'line-name', text: `${l.quantity} × ${t.name}` }),
          h('span', { class: 'line-price', text: pounds(l.unitPence * l.quantity) })), t.details ? h('small', { class: 'line-details', text: t.details }) : null);
      })) : null,
      h('dl', { class: 'totals' },
        h('dt', { text: 'Subtotal' }), h('dd', { text: pounds(o.goodsPence) }),
        o.type === 'Delivery' ? [h('dt', { text: 'Delivery fee' }), h('dd', { text: o.deliveryPence ? pounds(o.deliveryPence) : 'Free' })] : null,
        h('dt', { class: 'grand', text: 'Total' }), h('dd', { class: 'grand', text: pounds(o.totalPence) })),
      h('p', { class: 'pay-note', text: `To pay on ${o.type === 'Delivery' ? 'delivery' : 'collection'}: ${pounds(o.totalPence)}${o.deliveryPence ? ` (includes ${pounds(o.deliveryPence)} delivery)` : ''}. Cash or card.` }));
  }

  return ['Completed', 'Rejected', 'Cancelled', 'NotReceived'].includes(o.status);
}

async function check() {
  try {
    const res = await fetch(`/api/orders/${encodeURIComponent(id)}?t=${encodeURIComponent(token)}`);
    if (!res.ok) { $('headline').textContent = 'This order link is not right. Check the link, or phone the shop.'; return; }
    if (!render(await res.json())) setTimeout(check, 10000);
  } catch {
    setTimeout(check, 15000); // offline for a moment
  }
}

async function start() {
  // The menu gives the items' names; the shop gives its name and phone. Neither is needed to follow the order.
  try {
    const [shop, m] = await Promise.all([fetch('/api/shop').then(r => r.json()), fetch('/api/menu').then(r => r.json())]);
    menu = m;
    if (shop.configured) {
      $('brand').textContent = shop.name;
      if (shop.phone) $('help').replaceChildren('Questions about your order? Call ', h('a', { href: `tel:${shop.phone.replace(/\s/g, '')}`, text: shop.phone }), '.');
    }
  } catch { /* the order still shows */ }

  check();
}

if (id && token) start();
else $('headline').textContent = 'This order link is not right.';
