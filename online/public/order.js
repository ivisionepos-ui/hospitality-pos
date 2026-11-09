// The customer's order page: what is happening to the order, checked every 10 seconds until it is finished.
import { pounds } from '/rules.js';

const params = new URLSearchParams(location.search);
const id = params.get('id');
const token = params.get('t');
const $ = x => document.getElementById(x);
const time = iso => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/London' });

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
  $('title').textContent = o.number ? `Order ${o.number}` : 'Your order';
  $('headline').textContent = (HEADLINE[o.status] ?? (() => o.status))(o);
  const flow = o.type === 'Delivery' ? ['Received', 'Accepted', 'Ready', 'OutForDelivery', 'Completed'] : ['Received', 'Accepted', 'Ready', 'Completed'];
  const labels = { Received: 'Sent to the shop', Accepted: 'Accepted', Ready: 'Ready', OutForDelivery: 'On its way', Completed: o.type === 'Delivery' ? 'Delivered' : 'Collected' };
  const at = flow.indexOf(o.status);
  $('steps').replaceChildren(...(['Rejected', 'Cancelled', 'NotReceived'].includes(o.status) ? [] : flow.map((s, i) => {
    const li = document.createElement('li');
    li.textContent = labels[s];
    if (i < at || o.status === 'Completed') li.className = 'done';
    else if (i === at) li.className = 'now';
    return li;
  })));
  const pay = document.createElement('p');
  pay.textContent = `To pay on ${o.type === 'Delivery' ? 'delivery' : 'collection'}: ${pounds(o.totalPence)}${o.deliveryPence ? ` (includes ${pounds(o.deliveryPence)} delivery)` : ''}.`;
  $('details').replaceChildren(pay);
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

if (id && token) check();
else $('headline').textContent = 'This order link is not right.';
