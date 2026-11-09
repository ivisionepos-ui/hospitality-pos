// Website orders on the till: a flashing button and a beep while any wait to be accepted; accept with a ready time
// (the customer sees it on the website) and the order goes to the kitchen, or turn it down with a reason.
import { api, ApiError } from './api.js';
import { h, money, text, toast } from './dom.js';
import { busy, openDialog, textDialog } from './dialogs.js';

const WAITING = o => o.source === 'Website' && (o.status === 'Received' || o.status === 'Pending');

function beep() {
  try {
    beep.audio ??= new AudioContext();
    for (const [at, hz] of [[0, 880], [0.25, 1175]]) {
      const tone = beep.audio.createOscillator();
      const volume = beep.audio.createGain();
      tone.frequency.value = hz;
      volume.gain.value = 0.25;
      tone.connect(volume).connect(beep.audio.destination);
      tone.start(beep.audio.currentTime + at);
      tone.stop(beep.audio.currentTime + at + 0.2);
    }
  } catch { /* no sound available */ }
}

/** Keeps the till's "Website order" button up to date and beeps every 20 seconds while orders wait. */
export class WebOrders {
  constructor(onChanged) {
    this.onChanged = onChanged;
    this.waiting = [];
    this.button = h('button', { class: 'top-btn web-alert', hidden: true, onclick: () => this.open() });
    this.timer = null;
  }

  /** Called with today's orders whenever they change. */
  update(orders) {
    const before = new Set(this.waiting.map(o => o.id));
    this.waiting = orders.filter(WAITING).sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
    this.button.hidden = this.waiting.length === 0;
    this.button.textContent = this.waiting.length === 1 ? `Website order ${this.waiting[0].number}` : `${this.waiting.length} website orders`;
    if (this.waiting.some(o => !before.has(o.id))) beep();
    if (this.waiting.length && !this.timer) this.timer = setInterval(() => { if (document.body.contains(this.button) && this.waiting.length) beep(); }, 20000);
    if (!this.waiting.length && this.timer) { clearInterval(this.timer); this.timer = null; }
  }

  async open() {
    const order = this.waiting[0];
    if (!order) return;
    const usual = order.type === 'Delivery' ? 45 : 20;
    let minutes = usual;
    const lines = order.lines.map(l => h('li', {},
      h('strong', { text: `${l.quantity} × ${[l.size ? text(l.size) : null, text(l.name)].filter(Boolean).join(' ')}` }),
      l.extras.length ? h('span', { class: 'field-hint', text: ` ${l.extras.map(text).join(', ')}` }) : null,
      l.note ? h('span', { class: 'field-hint', text: ` “${l.note}”` }) : null));
    const c = order.contact ?? {};
    const choice = await openDialog(close => {
      const chips = h('div', { class: 'chips', role: 'radiogroup', 'aria-label': 'Ready in' });
      const renderChips = () => chips.replaceChildren(...[10, 15, 20, 30, 45, 60, 75, 90].map(m => h('button', {
        type: 'button', class: `chip ${m === minutes ? 'chip-on' : ''}`, role: 'radio', 'aria-checked': String(m === minutes),
        onclick: () => { minutes = m; renderChips(); }, text: `${m} min`,
      })));
      renderChips();
      return {
        title: `Website order ${order.number} · ${order.type === 'Delivery' ? 'Delivery' : 'Collection'}`,
        class: 'dialog-web-order',
        body: [
          h('p', { class: 'web-customer' }, h('strong', { text: c.name ?? '' }), ` · ${c.phone ?? ''}`,
            c.address ? h('span', { text: ` · ${[c.address.line1, c.address.town, c.address.postcode].filter(Boolean).join(', ')}` }) : null),
          c.notes ? h('p', { class: 'field-hint', text: `Note: ${c.notes}` }) : null,
          h('ul', { class: 'web-lines' }, lines),
          h('p', {}, h('strong', { text: `Total ${money(order.total)}` }), ' · to pay on ', order.type === 'Delivery' ? 'delivery' : 'collection'),
          h('div', { class: 'field' }, h('span', { class: 'field-label', text: order.type === 'Delivery' ? 'Delivered in' : 'Ready in' }), chips),
        ],
        actions: [
          h('button', { class: 'btn btn-danger-soft', onclick: () => close('reject'), text: 'Turn down' }),
          h('button', { class: 'btn', onclick: () => close(null), text: 'Later' }),
          h('button', { class: 'btn btn-primary', onclick: () => close('accept'), text: 'Accept and send to kitchen' }),
        ],
      };
    });

    try {
      if (choice === 'accept') {
        await busy(async () => {
          await api.post(`/api/orders/${order.id}/status`, { status: 'Accepted', reason: `Ready in ${minutes} minutes` });
          await api.post(`/api/orders/${order.id}/send-to-kitchen`, {});
        });
        toast(`Website order ${order.number} accepted. The customer sees it ready in about ${minutes} minutes.`, 'ok', 6000);
      } else if (choice === 'reject') {
        const reason = await textDialog(`Turn down website order ${order.number}`, {
          label: 'Reason (the customer sees it)', suggestions: ['Too busy right now', 'An item has sold out', 'Closing soon', 'Too far to deliver'], ok: 'Turn down',
        });
        if (!reason) return;
        await busy(() => api.post(`/api/orders/${order.id}/status`, { status: 'Rejected', reason }));
        toast(`Website order ${order.number} turned down.`, 'info', 5000);
      } else {
        return;
      }
    } catch (e) {
      toast(e instanceof ApiError ? e.message : 'Could not change the order.', 'error', 8000);
    }

    await this.onChanged();
  }
}
