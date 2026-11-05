// The kitchen screen: orders as cards, oldest first, updated live. Start → Ready with one tap each.
import { api, ApiError } from './api.js';
import { h, mount, text, timeText, toast } from './dom.js';
import { confirmDialog, openDialog } from './dialogs.js';
import { soldOutDialog } from './editmenu.js';
import { MenuView } from './menu.js';
import { changeMyPin } from './staff.js';

const COOKING = ['Accepted', 'Preparing'];
const NEW_FOR_MS = 3 * 60 * 1000; // items added to an order show "NEW" for 3 minutes
const AMBER_MINUTES = 10;
const RED_MINUTES = 20;

const TYPE = { Takeaway: 'TAKEAWAY', EatIn: 'EAT IN', Delivery: 'DELIVERY' };

/** A short beep for a new order (no sound files needed). */
function beep() {
  try {
    beep.audio ??= new AudioContext();
    const tone = beep.audio.createOscillator();
    const volume = beep.audio.createGain();
    tone.frequency.value = 880;
    volume.gain.value = 0.25;
    tone.connect(volume).connect(beep.audio.destination);
    tone.start();
    tone.stop(beep.audio.currentTime + 0.3);
  } catch { /* no sound available */ }
}

function minutesSince(iso) {
  return Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 60000));
}

export class Kitchen {
  /**
   * @param {HTMLElement} root
   * @param {{ me: object, device: object, logout: () => void }} ctx
   */
  constructor(root, ctx) {
    this.root = root;
    this.ctx = ctx;
    this.orders = new Map();     // id → order (as the hub sends it)
    this.firstSeen = new Map();  // line id → time first shown here (for "NEW")
    this.ticked = new Set();     // line ids the cook has ticked off (this screen only)
    this.live = false;
    this.ready = [];             // recently ready orders, newest first, for Recall
  }

  async start() {
    await this.reload();
    this.timer = setInterval(() => { if (document.body.contains(this.board)) this.renderBoard(); else clearInterval(this.timer); }, 15000);
  }

  async reload() {
    const today = await api.get('/api/orders');
    const now = Date.now();
    for (const order of today) {
      this.orders.set(order.id, order);
      for (const line of order.lines) if (line.sentToKitchen && !this.firstSeen.has(line.id)) this.firstSeen.set(line.id, now - NEW_FOR_MS); // not "new" on first load
    }

    this.ready = today.filter(o => o.status === 'Ready').sort((a, b) => b.number.localeCompare(a.number));
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
    if (event.kind === 'MenuChanged' || !event.order) return;
    const order = event.order;
    const before = this.orders.get(order.id);
    const hadSent = before ? before.lines.some(l => l.sentToKitchen) : false;
    const now = Date.now();
    // "NEW" is only for items added to an order already on the board, not for a whole new order.
    const firstShown = hadSent ? now : now - NEW_FOR_MS;
    // Count from when an item is first sent to the kitchen (orders are created a moment before they are sent).
    for (const line of order.lines) if (line.sentToKitchen && !this.firstSeen.has(line.id)) this.firstSeen.set(line.id, firstShown);
    this.orders.set(order.id, order);
    if (COOKING.includes(order.status) && order.lines.some(l => l.sentToKitchen) && (!hadSent || order.lines.some(l => l.sentToKitchen && !before.lines.find(b => b.id === l.id)?.sentToKitchen))) {
      beep(); // a new order, or more items for one
    }

    this.ready = this.ready.filter(o => o.id !== order.id);
    if (order.status === 'Ready') this.ready.unshift(order);
    this.renderBoard();
  }

  // ---------- Layout ----------

  render() {
    this.board = h('main', { class: 'kds-board', 'aria-label': 'Orders to make' });
    this.readyBar = h('div', { class: 'kds-ready' });
    mount(this.root,
      h('header', { class: 'topbar kds-top' },
        h('strong', { text: `Kitchen · ${this.ctx.device?.name ?? ''}` }),
        h('span', { class: `live ${this.live ? 'live-on' : 'live-off'}`, text: this.live ? 'Live' : 'Connecting…' }),
        h('span', { class: 'kds-count' }),
        h('div', { class: 'topbar-actions' },
          h('button', { class: 'top-btn', onclick: () => this.recall(), text: 'Recall' }),
          h('button', { class: 'top-btn', onclick: () => this.soldOut(), text: 'Sold out' }),
          h('span', { class: 'staff-name', text: this.ctx.me.staff.name }),
          h('button', { class: 'top-btn top-btn-quiet', onclick: () => changeMyPin(this.ctx.me.staff, this.ctx.logout).catch(e => toast(e.message, 'error')), text: 'My PIN' }),
          h('button', { class: 'top-btn top-btn-quiet', onclick: () => this.ctx.logout(), text: 'Log out' }))),
      this.board,
      this.readyBar);
    this.renderBoard();
  }

  /** Sold out / back in stock, the same panel as on the tills. */
  async soldOut() {
    try {
      const menu = new MenuView(await api.get('/api/menu'));
      await soldOutDialog({ menu, categoryId: menu.categories()[0]?.id ?? null, renderCatalog() {} });
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  cooking() {
    return [...this.orders.values()]
      .filter(o => COOKING.includes(o.status) && o.lines.some(l => l.sentToKitchen))
      .sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
  }

  renderBoard() {
    if (!this.board) return;
    const cooking = this.cooking();
    const count = this.root.querySelector('.kds-count');
    if (count) count.textContent = `${cooking.length} to make · ${this.ready.length} ready`;
    mount(this.board, cooking.length
      ? cooking.map(o => this.card(o))
      : h('div', { class: 'kds-empty' }, h('p', { text: 'No orders to make.' }), h('p', { class: 'kds-empty-sub', text: 'New orders appear here as soon as the till sends them.' })));
    mount(this.readyBar,
      h('span', { class: 'kds-ready-label', text: 'Ready:' }),
      this.ready.length
        ? this.ready.slice(0, 8).map(o => h('button', { class: 'kds-ready-chip', onclick: () => this.recall(o), text: o.number }))
        : h('span', { class: 'kds-ready-none', text: 'none yet' }));
  }

  card(order) {
    const minutes = minutesSince(order.createdAt);
    const age = minutes >= RED_MINUTES ? 'kds-late' : minutes >= AMBER_MINUTES ? 'kds-slow' : '';
    const lines = order.lines.filter(l => l.sentToKitchen);
    const now = Date.now();
    const who = order.contact?.name ?? null;
    return h('article', { class: `kds-card ${order.status === 'Preparing' ? 'kds-preparing' : 'kds-new'} ${age}`, 'aria-label': `Order ${order.number}` },
      h('header', { class: 'kds-head' },
        h('span', { class: 'kds-number', text: order.number }),
        h('span', { class: 'kds-type', text: TYPE[order.type] ?? order.type }),
        h('span', { class: 'kds-time', title: `Ordered ${timeText(order.createdAt)}`, text: `${minutes} min` })),
      who || order.source === 'Phone' ? h('div', { class: 'kds-who', text: [who, order.source === 'Phone' && order.type !== 'Delivery' ? 'Collection' : null].filter(Boolean).join(' · ') }) : null,
      h('ol', { class: 'kds-lines' }, lines.map(line => {
        const isNew = now - (this.firstSeen.get(line.id) ?? 0) < NEW_FOR_MS && !line.isVoided;
        const done = this.ticked.has(line.id);
        const details = [
          ...line.extras.map(e => (/^no\b/i.test(text(e)) ? text(e) : `+ ${text(e)}`)),
          ...line.dealItems.map(d => [d.size ? text(d.size) : null, text(d.name)].filter(Boolean).join(' ') + (d.extras?.length ? ` (${d.extras.map(text).join(', ')})` : '')),
        ];
        return h('li', {
          class: `kds-line ${line.isVoided ? 'kds-void' : ''} ${done ? 'kds-done' : ''}`,
          onclick: () => { if (line.isVoided) return; if (done) this.ticked.delete(line.id); else this.ticked.add(line.id); this.renderBoard(); },
        },
        h('span', { class: 'kds-qty', text: String(line.quantity) }),
        h('div', { class: 'kds-item' },
          h('span', { class: 'kds-name', text: [line.size ? text(line.size) : null, text(line.name)].filter(Boolean).join(' ') }),
          line.isVoided ? h('span', { class: 'kds-flag kds-flag-void', text: 'REMOVED – DO NOT MAKE' }) : isNew ? h('span', { class: 'kds-flag kds-flag-new', text: 'NEW' }) : null,
          details.length ? h('ul', { class: 'kds-details' }, details.map(d => h('li', { text: d }))) : null,
          line.note ? h('span', { class: 'kds-note', text: line.note }) : null));
      })),
      order.contact?.notes && order.type !== 'Delivery' ? h('p', { class: 'kds-note', text: order.contact.notes }) : null,
      h('footer', { class: 'kds-actions' },
        order.status === 'Accepted' ? h('button', { class: 'btn kds-start', onclick: () => this.move(order, 'Preparing'), text: 'Start' }) : null,
        h('button', { class: 'btn btn-primary kds-bump', onclick: () => this.move(order, 'Ready'), text: 'Ready' })));
  }

  // ---------- Actions ----------

  async move(order, status) {
    try {
      const updated = await api.post(`/api/orders/${order.id}/status`, { status });
      this.onEvent({ kind: 'OrderUpdated', order: updated });
      if (status === 'Ready') toast(`Order ${order.number} ready.`, 'ok', 2000);
    } catch (e) {
      toast(e instanceof ApiError ? e.message : 'Could not change the order.', 'error', 6000);
      try { await this.reload(); } catch { /* shown when the hub is back */ }
    }
  }

  /** Put an order marked ready by mistake back on the board. */
  async recall(order) {
    let target = order;
    if (!target) {
      if (!this.ready.length) { toast('No ready orders to recall.', 'info'); return; }
      target = await openDialog(close => ({
        title: 'Recall an order',
        class: 'dialog-narrow',
        body: h('div', { class: 'menu-list' }, this.ready.slice(0, 12).map(o => h('button', {
          class: 'btn menu-btn', onclick: () => close(o),
          text: `${o.number} · ${TYPE[o.type] ?? o.type}${o.contact?.name ? ` · ${o.contact.name}` : ''}`,
        }))),
      }));
      if (!target) return;
    } else if (!(await confirmDialog(`Recall order ${target.number}?`, 'It goes back on the board as preparing.', 'Recall'))) {
      return;
    }

    await this.move(target, 'Preparing');
  }
}
