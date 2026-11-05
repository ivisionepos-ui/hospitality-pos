// Deliveries and drivers: send ready deliveries out with a driver, take the money when they come back,
// bring back what could not be delivered, and each driver's day. Also the driver's own "My deliveries" screen.
import { api, ApiError } from './api.js';
import { h, mount, money, newId, penceText, statusText, timeText, toast } from './dom.js';
import { busy, openDialog, textDialog } from './dialogs.js';
import { ensureShift } from './pay.js';

const WAITING = ['Accepted', 'Preparing', 'Ready'];
const BACK_REASONS = ['Nobody home', 'Wrong address', 'Customer cancelled', 'Driver could not go'];

const due = o => Math.max(0, o.balance?.minorUnits ?? 0);
const minutesSince = iso => Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 60000));
const place = o => [o.contact?.address?.line1, o.contact?.address?.postcode].filter(Boolean).join(', ');

function problemText(e) {
  return e instanceof ApiError ? e.message : 'Could not reach the hub.';
}

/** The Drivers screen on the till. */
export async function driversDialog() {
  let orders = [];
  let drivers = [];
  let day = [];
  const picked = new Set();
  const load = async () => {
    [orders, drivers, day] = await Promise.all([api.get('/api/orders'), api.get('/api/drivers'), api.get('/api/drivers/today')]);
    const waiting = new Set(orders.filter(o => o.type === 'Delivery' && WAITING.includes(o.status)).map(o => o.id));
    for (const id of [...picked]) if (!waiting.has(id)) picked.delete(id);
  };
  try {
    await busy(load);
  } catch (e) {
    toast(problemText(e), 'error', 6000);
    return;
  }

  await openDialog(close => {
    const body = h('div', { class: 'drivers' });
    const nameOf = id => drivers.find(d => d.id === id)?.name ?? day.find(d => d.driverId === id)?.name ?? 'Driver';
    const act = work => async () => {
      try {
        await work();
        await load();
      } catch (e) {
        toast(problemText(e), 'error', 6000);
        try { await load(); } catch { /* shown above */ }
      }
      render();
    };

    const sendWith = driver => act(async () => {
      const ids = [...picked];
      const problems = [];
      await busy(async () => {
        for (const id of ids) {
          try {
            await api.post(`/api/orders/${id}/send-out`, { driverId: driver.id });
            picked.delete(id);
          } catch (e) {
            problems.push(problemText(e));
          }
        }
      });
      const sent = ids.length - problems.length;
      if (sent) toast(`${sent} ${sent === 1 ? 'delivery' : 'deliveries'} out with ${driver.name}.`, 'ok');
      if (problems.length) toast(problems.join(' '), 'error', 8000);
    });

    // The driver is back: take the money they collected (cash into this drawer, or card on their machine), then done.
    const delivered = (order, method) => act(async () => {
      const amount = due(order);
      if (method && !(await ensureShift())) return;
      await busy(async () => {
        if (method === 'cash') await api.post(`/api/orders/${order.id}/payments/cash`, { tenderedPence: amount, paymentId: newId() });
        if (method === 'card') await api.post(`/api/orders/${order.id}/payments/card`, { amountPence: amount, paymentId: newId() });
        await api.post(`/api/orders/${order.id}/status`, { status: 'Completed' });
      });
      toast(`Order ${order.number} delivered${method ? `: ${penceText(amount)} ${method} from ${nameOf(order.driverId)}` : ''}.`, 'ok');
    });

    const backToShop = order => act(async () => {
      const reason = await textDialog(`Order ${order.number} back in the shop?`, { label: 'Why', suggestions: BACK_REASONS, ok: 'Back to shop' });
      if (!reason) return;
      await busy(() => api.post(`/api/orders/${order.id}/back-to-shop`, { reason }));
      toast(`Order ${order.number} is back in the shop.`, 'ok');
    });

    const waitingRow = o => {
      const on = picked.has(o.id);
      return h('button', {
        class: `delivery-row ${on ? 'delivery-on' : ''}`, 'aria-pressed': String(on), 'aria-label': `Order ${o.number}`,
        onclick: () => { if (on) picked.delete(o.id); else picked.add(o.id); render(); },
      },
      h('span', { class: 'delivery-tick', text: on ? '✓' : '' }),
      h('strong', { class: 'order-no', text: o.number }),
      h('span', { class: 'delivery-place', text: [o.contact?.name, place(o)].filter(Boolean).join(' · ') }),
      h('span', { class: `status status-${o.status.toLowerCase()}`, text: statusText(o.status) }),
      h('span', { class: 'delivery-age', text: `${minutesSince(o.createdAt)} min` }),
      h('span', { class: `paid-badge ${due(o) ? 'paid-due' : 'paid-ok'}`, text: due(o) ? `Collect ${penceText(due(o))}` : 'Paid' }));
    };

    const outRow = o => h('div', { class: 'delivery-row delivery-out', 'aria-label': `Order ${o.number}` },
      h('strong', { class: 'order-no', text: o.number }),
      h('span', { class: 'delivery-place', text: [o.contact?.name, place(o)].filter(Boolean).join(' · ') }),
      h('span', { class: `paid-badge ${due(o) ? 'paid-due' : 'paid-ok'}`, text: due(o) ? `Collect ${penceText(due(o))}` : 'Paid' }),
      h('div', { class: 'delivery-actions' },
        due(o) ? h('button', { class: 'btn btn-small btn-primary', onclick: delivered(o, 'cash'), text: `Cash ${penceText(due(o))}` }) : null,
        due(o) ? h('button', { class: 'btn btn-small', onclick: delivered(o, 'card'), text: 'Card' }) : null,
        due(o) ? null : h('button', { class: 'btn btn-small btn-primary', onclick: delivered(o, null), text: 'Delivered' }),
        h('button', { class: 'btn btn-small btn-quiet', onclick: backToShop(o), text: 'Back' })));

    const render = () => {
      const waiting = orders.filter(o => o.type === 'Delivery' && WAITING.includes(o.status)).sort((a, b) => a.number.localeCompare(b.number));
      const out = orders.filter(o => o.status === 'OutForDelivery');
      const byDriver = [...new Set(out.map(o => o.driverId))].map(id => ({ id, orders: out.filter(o => o.driverId === id) }));
      const regular = drivers.filter(d => d.role === 'Driver');
      const others = drivers.filter(d => d.role !== 'Driver');
      const count = picked.size;
      mount(body,
        h('section', { class: 'drivers-section' },
          h('h3', { class: 'menu-heading', text: `Ready to go out (${waiting.length})` }),
          waiting.length ? h('div', { class: 'delivery-list' }, waiting.map(waitingRow)) : h('p', { class: 'empty-note', text: 'No deliveries waiting.' }),
          waiting.length ? h('div', { class: 'send-with' },
            h('span', { class: 'field-label', text: count ? `Send ${count} with:` : 'Tap the orders, then the driver:' }),
            regular.length ? null : h('span', { class: 'field-hint', text: 'No drivers yet: add them in More → Staff (role Driver).' }),
            regular.map(d => h('button', { class: 'btn btn-kitchen', disabled: !count, onclick: sendWith(d), text: d.name })),
            others.length ? h('details', { class: 'other-drivers' }, h('summary', { text: 'Someone else' }),
              others.map(d => h('button', { class: 'btn btn-small', disabled: !count, onclick: sendWith(d), text: d.name }))) : null) : null),
        h('section', { class: 'drivers-section' },
          h('h3', { class: 'menu-heading', text: `Out now (${out.length})` }),
          byDriver.length ? byDriver.map(g => h('div', { class: 'driver-group', 'aria-label': nameOf(g.id) },
            h('div', { class: 'driver-head' },
              h('strong', { text: nameOf(g.id) }),
              h('span', { class: 'field-hint', text: `${g.orders.length} out · collect ${penceText(g.orders.reduce((s, o) => s + due(o), 0))}` })),
            g.orders.map(outRow))) : h('p', { class: 'empty-note', text: 'Nobody is out.' })),
        day.length ? h('section', { class: 'drivers-section' },
          h('h3', { class: 'menu-heading', text: 'Today' }),
          h('table', { class: 'report drivers-day' },
            h('thead', {}, h('tr', {}, ['Driver', 'Delivered', 'Out', 'Cash handed in', 'Card'].map(t => h('th', { scope: 'col', text: t })))),
            h('tbody', {}, day.map(d => h('tr', {},
              h('td', { text: d.name }), h('td', { text: String(d.delivered) }),
              h('td', { text: d.out ? `${d.out} (collect ${money(d.toCollect)})` : '0' }),
              h('td', { text: money(d.cash) }), h('td', { text: money(d.card) })))))) : null);
    };
    render();
    return {
      title: 'Deliveries and drivers',
      wide: true,
      body,
      actions: [
        h('button', { class: 'btn', onclick: act(async () => {}), text: 'Refresh' }),
        h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' }),
      ],
    };
  });
}

/** What a driver sees when they log in: the deliveries they are taking, with addresses and what to collect. */
export class DriverScreen {
  constructor(root, ctx) {
    this.root = root;
    this.ctx = ctx;
    this.orders = [];
    this.live = false;
  }

  async start() {
    await this.reload();
  }

  async reload() {
    this.orders = (await api.get('/api/orders')).filter(o => o.status === 'OutForDelivery' && o.driverId === this.ctx.me.staff.id);
    this.render();
  }

  setLive(connected) {
    this.live = connected;
    const dot = this.root.querySelector('.live');
    if (dot) {
      dot.className = `live ${connected ? 'live-on' : 'live-off'}`;
      dot.textContent = connected ? 'Live' : 'Reconnecting…';
    }
  }

  onEvent(event) {
    if (event.order) this.reload().catch(() => {});
  }

  render() {
    mount(this.root,
      h('header', { class: 'topbar' },
        h('strong', { text: `My deliveries · ${this.ctx.me.staff.name}` }),
        h('span', { class: `live ${this.live ? 'live-on' : 'live-off'}`, text: this.live ? 'Live' : 'Connecting…' }),
        h('div', { class: 'topbar-actions' },
          h('button', { class: 'top-btn top-btn-quiet', onclick: () => this.ctx.logout(), text: 'Log out' }))),
      h('main', { class: 'driver-board' },
        this.orders.length ? this.orders.map(o => h('article', { class: 'driver-card', 'aria-label': `Order ${o.number}` },
          h('header', { class: 'driver-card-head' },
            h('span', { class: 'kds-number', text: o.number }),
            h('span', { class: `paid-badge ${due(o) ? 'paid-due' : 'paid-ok'}`, text: due(o) ? `COLLECT ${penceText(due(o))}` : 'PAID' })),
          h('p', { class: 'driver-name', text: o.contact?.name ?? '' }),
          h('p', { class: 'driver-address', text: [o.contact?.address?.line1, o.contact?.address?.line2, o.contact?.address?.town, o.contact?.address?.postcode].filter(Boolean).join(', ') }),
          o.contact?.phone ? h('a', { class: 'driver-phone', href: `tel:${o.contact.phone.replace(/\s/g, '')}`, text: o.contact.phone }) : null,
          o.contact?.notes ? h('p', { class: 'kds-note', text: o.contact.notes }) : null,
          h('p', { class: 'field-hint', text: `Ordered ${timeText(o.createdAt)}` })))
          : h('div', { class: 'kds-empty' }, h('p', { text: 'No deliveries with you.' }), h('p', { class: 'kds-empty-sub', text: 'The shop sends orders out with you on the till; they appear here.' }))));
  }
}
