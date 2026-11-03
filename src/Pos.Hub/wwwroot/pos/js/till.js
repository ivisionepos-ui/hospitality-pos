// The order screen: categories and item buttons on the right, the order on the left.
// An order being built lives only on this till until it is sent or paid; the hub prices it live (quote).
import { api, ApiError } from './api.js';
import { h, mount, money, newId, penceText, text, timeText, toast } from './dom.js';
import { alertDialog, amountDialog, busy, confirmDialog, managerPinDialog, openDialog, textDialog } from './dialogs.js';
import { contactSummary, customerDialog } from './customer.js';
import { dealDialog, itemOptionsDialog } from './item-dialogs.js';
import { MenuEditor } from './editmenu.js';
import { MenuView } from './menu.js';
import { moreDialog } from './more.js';
import { payDialog } from './pay.js';

const ORDER_MODES = [
  { id: 'counter', label: 'Takeaway', source: 'Counter', type: 'Takeaway' },
  { id: 'eatin', label: 'Eat in', source: 'Counter', type: 'EatIn' },
  { id: 'phone', label: 'Collection', source: 'Phone', type: 'Takeaway' },
  { id: 'delivery', label: 'Delivery', source: 'Phone', type: 'Delivery' },
];
const modeLabel = (source, type) => ORDER_MODES.find(m => m.source === source && m.type === type)?.label ?? type;
const CLOSED = ['Completed', 'Cancelled', 'Rejected'];
const VOID_REASONS = ['Customer changed mind', 'Entered by mistake', 'Out of stock'];

/** Runs a request; when the hub says a manager must approve, asks for a manager PIN and tries again. */
export async function withApproval(send) {
  try {
    return await send(undefined);
  } catch (e) {
    if (!(e instanceof ApiError) || e.code !== 'ApprovalRequired') throw e;
    for (;;) {
      const pin = await managerPinDialog(e.message.replace(/ Ask a manager.*$/, ''));
      if (!pin) return null;
      try {
        return await send(pin);
      } catch (again) {
        if (!(again instanceof ApiError) || again.code !== 'WrongPin') throw again;
        toast(again.message, 'error');
      }
    }
  }
}

/** "+ Garlic", but "No onion" stays as it is. */
function extraText(name) {
  return /^no\b/i.test(name) ? name : `+ ${name}`;
}

function lineRequest(line) {
  return {
    productId: line.productId,
    variantId: line.variantId,
    quantity: line.quantity,
    extras: line.extras.length ? line.extras : null,
    choices: line.choices ? line.choices.map(c => ({ slotId: c.slotId, productId: c.productId, variantId: c.variantId, extras: c.extras })) : null,
    note: line.note,
  };
}

export class Till {
  /**
   * @param {HTMLElement} root
   * @param {{ me: object, device: object, logout: () => void }} ctx
   */
  constructor(root, ctx) {
    this.root = root;
    this.ctx = ctx;
    this.menu = null;
    this.categoryId = null;
    this.mode = ORDER_MODES[0];
    this.saved = null;       // the saved order being looked at, or null for a new one
    this.selected = null;    // selected line: { kind: 'draft' | 'saved', key }
    this.quote = null;
    this.quoteSeq = 0;
    this.openCount = 0;
    this.live = false;
    this.newDraft();
  }

  can(permission) {
    return this.ctx.me.permissions.includes(permission);
  }

  newDraft() {
    // customer: { contact, distanceMiles } for phone and delivery orders; feeOverride: { pence, reason } (needs a manager).
    this.draft = { orderId: newId(), key: newId(), lines: [], customer: null, feeOverride: null };
    this.quote = null;
  }

  async start() {
    await this.loadMenu();
    this.render();
    this.refreshOpenCount();
  }

  async loadMenu() {
    this.menu = new MenuView(await api.get('/api/menu'));
    const categories = this.menu.categories();
    if (!categories.some(c => c.id === this.categoryId)) this.categoryId = categories[0]?.id ?? null;
  }

  setLive(connected) {
    this.live = connected;
    const dot = this.root.querySelector('.live');
    if (dot) {
      dot.className = `live ${connected ? 'live-on' : 'live-off'}`;
      dot.textContent = connected ? 'Live' : 'Reconnecting…';
    }
  }

  /** Something changed on the hub (another till, the kitchen, a printer). */
  async onEvent(event) {
    switch (event.kind) {
      case 'MenuChanged':
        await this.loadMenu();
        if (this.editor) {
          this.editor.renderCatalog(); // keep the edit panel as it is; a clash is caught when saving
          break;
        }

        this.render();
        if (this.draft.lines.length) this.requestQuote();
        break;
      case 'OrderCreated':
      case 'OrderUpdated':
      case 'PaymentChanged':
        if (this.saved && event.order?.id === this.saved.id) {
          const id = this.saved.id;
          const fresh = await api.get(`/api/orders/${id}`);
          // Only if the same order is still on screen (the cashier may have moved on meanwhile).
          if (this.saved?.id === id) {
            this.saved = fresh;
            this.renderTicket();
          }
        }
        this.refreshOpenCount();
        break;
      case 'PrintFailed':
      case 'BackupFailed':
        toast(event.summary ?? 'Problem on the hub.', 'error', 10000);
        break;
      default:
        break;
    }
  }

  async refreshOpenCount() {
    try {
      const orders = await api.get('/api/orders');
      this.openCount = orders.filter(o => !CLOSED.includes(o.status)).length;
      const badge = this.root.querySelector('.orders-count');
      if (badge) badge.textContent = this.openCount ? String(this.openCount) : '';
    } catch { /* shown when the hub is reachable again */ }
  }

  // ---------- Layout ----------

  /** Edit menu mode: same layout, the left side becomes the edit panel. The order in progress is kept. */
  enterEditMode() {
    this.editor = new MenuEditor(this);
    this.render();
  }

  async leaveEditMode() {
    if (this.editor?.isDirty() && !(await confirmDialog('Leave without saving?', 'Your changes have not been saved.', 'Leave'))) return;
    this.editor = null;
    await this.loadMenu();
    this.render();
    if (this.draft.lines.length) this.requestQuote();
  }

  render() {
    this.ticketEl = h('section', { class: 'ticket', 'aria-label': this.editor ? 'Edit panel' : 'Order' });
    this.catalogEl = h('section', { class: 'catalog', 'aria-label': 'Menu' });
    if (this.editor) {
      mount(this.root,
        h('header', { class: 'topbar topbar-edit' },
          h('strong', { text: 'EDIT MENU' }),
          h('span', { class: 'edit-note', text: 'Changes go live on every till' }),
          h('div', { class: 'topbar-actions' },
            h('button', { class: 'top-btn', onclick: () => this.editor.undo(), text: 'Undo last' }),
            h('button', { class: 'top-btn', onclick: () => this.editor.showChanges(), text: 'Changes' }),
            h('button', { class: 'top-btn top-btn-done', onclick: () => this.leaveEditMode(), text: 'Done' }))),
        h('main', { class: 'till' }, this.ticketEl, this.catalogEl));
      this.editor.render();
      return;
    }

    mount(this.root,
      h('header', { class: 'topbar' },
        h('div', { class: 'brand' }, h('strong', { text: this.ctx.device?.name ?? 'Till' })),
        h('span', { class: `live ${this.live ? 'live-on' : 'live-off'}`, text: this.live ? 'Live' : 'Connecting…' }),
        h('div', { class: 'topbar-actions' },
          h('button', { class: 'top-btn', onclick: () => this.showOrders() }, 'Orders ', h('span', { class: 'orders-count', text: this.openCount ? String(this.openCount) : '' })),
          h('button', { class: 'top-btn', onclick: () => moreDialog(this) , text: 'More' }),
          h('span', { class: 'staff-name', text: this.ctx.me.staff.name }),
          h('button', { class: 'top-btn top-btn-quiet', onclick: () => this.ctx.logout(), text: 'Log out' }))),
      h('main', { class: 'till' }, this.ticketEl, this.catalogEl));
    this.renderTicket();
    this.renderCatalog();
  }

  renderCatalog() {
    if (this.editor) { this.editor.renderCatalog(); return; }
    if (!this.menu || this.menu.isEmpty) {
      mount(this.catalogEl, h('div', { class: 'empty' },
        h('h2', { text: 'The menu is empty' }),
        h('p', { text: 'Enter your menu in the back office, or load a sample takeaway menu to try the till.' }),
        this.can('EditMenu') ? h('button', { class: 'btn btn-primary btn-big', onclick: () => this.loadSampleMenu(), text: 'Load sample menu' }) : null));
      return;
    }

    const categories = this.menu.categories();
    const tabs = h('nav', { class: 'cats', 'aria-label': 'Categories' }, categories.map(c => h('button', {
      class: `cat ${c.id === this.categoryId ? 'cat-on' : ''}`,
      style: c.colour ? { '--cat': c.colour } : null,
      'aria-pressed': String(c.id === this.categoryId),
      onclick: () => { this.categoryId = c.id; this.renderCatalog(); },
      text: text(c.name),
    })));
    const category = categories.find(c => c.id === this.categoryId);
    const grid = h('div', { class: 'items' }, this.menu.productsIn(this.categoryId).map(p => {
      const soldOut = this.menu.isSoldOut(p);
      const price = p.variants.length > 1 ? `from ${penceText(this.menu.fromPrice(p))}` : penceText(p.variants[0].price.minorUnits);
      return h('button', {
        class: `item ${soldOut ? 'item-out' : ''}`,
        style: category?.colour ? { '--cat': category.colour } : null,
        disabled: soldOut,
        onclick: () => this.pick(p),
      }, h('span', { class: 'item-name', text: this.menu.buttonName(p) }),
        h('span', { class: 'item-price', text: soldOut ? 'Sold out' : price }));
    }));
    mount(this.catalogEl, tabs, grid);
  }

  async loadSampleMenu() {
    try {
      await busy(() => api.post('/api/menu/sample'));
      await this.loadMenu();
      this.renderCatalog();
      toast('Sample menu loaded.', 'ok');
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  // ---------- Adding items ----------

  async pick(product) {
    let line;
    if (this.menu.isDeal(product)) {
      const deal = await dealDialog(this.menu, product);
      if (!deal) return;
      line = {
        productId: product.id, variantId: deal.variantId, quantity: deal.quantity, extras: [], note: null,
        choices: deal.choices, name: text(product.name), size: null,
        details: deal.choices.map(c => c.label), unitPence: deal.unitPence,
      };
    } else if (this.menu.needsOptions(product)) {
      const chosen = await itemOptionsDialog(this.menu, product);
      if (!chosen) return;
      line = {
        productId: product.id, variantId: chosen.variantId, quantity: chosen.quantity, extras: chosen.extras, note: chosen.note,
        choices: null, name: text(product.name), size: chosen.summary.size, details: chosen.summary.extras.map(extraText), unitPence: chosen.unitPence,
      };
    } else {
      const variant = product.variants.find(v => !v.isSoldOut) ?? product.variants[0];
      // Same plain item again: one more on the existing line.
      const same = this.draft.lines.find(l => l.productId === product.id && l.variantId === variant.id && !l.extras.length && !l.choices && !l.note);
      if (same) {
        same.quantity++;
        this.selected = { kind: 'draft', key: same.key };
        this.draftChanged();
        return;
      }

      line = {
        productId: product.id, variantId: variant.id, quantity: 1, extras: [], note: null, choices: null,
        name: text(product.name), size: null, details: [], unitPence: variant.price.minorUnits,
      };
    }

    line.key = newId();
    this.draft.lines.push(line);
    this.selected = { kind: 'draft', key: line.key };
    this.draftChanged();
  }

  draftChanged() {
    this.renderTicket();
    this.requestQuote();
  }

  /** Asks the hub for the real totals (offers, deals, VAT). Older answers are ignored. */
  requestQuote() {
    const seq = ++this.quoteSeq;
    clearTimeout(this.quoteTimer);
    if (!this.draft.lines.length) {
      this.quote = null;
      this.renderTicket();
      return;
    }

    this.quoteTimer = setTimeout(async () => {
      const mode = this.saved ? { source: this.saved.source, type: this.saved.type } : this.mode;
      try {
        const quote = await api.post('/api/orders/quote', {
          orderId: this.draft.orderId, idempotencyKey: this.draft.key, source: mode.source, type: mode.type,
          lines: this.draft.lines.map(lineRequest),
          delivery: this.saved ? null : this.deliveryRequest(),
        });
        if (seq === this.quoteSeq) { this.quote = quote; this.renderTicket(); }
      } catch (e) {
        if (seq === this.quoteSeq) { this.quote = null; this.renderTicket(); toast(e.message, 'error'); }
      }
    }, 120);
  }

  // ---------- The order (left) ----------

  renderTicket() {
    if (this.editor) return;
    const saved = this.saved;
    const draft = this.draft.lines;
    const head = saved
      ? h('div', { class: 'ticket-head' },
        h('div', {}, h('span', { class: 'ticket-number', text: `Order ${saved.number}` }),
          h('span', { class: 'ticket-meta', text: `${modeLabel(saved.source, saved.type)} · ${timeText(saved.createdAt)}` }),
          saved.contact ? h('span', { class: 'ticket-customer', text: contactSummary(saved.contact) }) : null),
        h('span', { class: `status status-${saved.status.toLowerCase()}`, text: saved.status }))
      : h('div', { class: 'ticket-head' },
        h('span', { class: 'ticket-number', text: 'New order' }),
        h('div', { class: 'seg seg-small' }, ORDER_MODES.map(m => h('button', {
          class: `seg-btn ${this.mode.id === m.id ? 'seg-on' : ''}`, 'aria-pressed': String(this.mode.id === m.id),
          onclick: () => { this.mode = m; this.renderTicket(); this.requestQuote(); if (m.id === 'delivery' && !this.draft.customer) this.editCustomer(); }, text: m.label,
        }))),
        this.customerBox());

    const rows = [];
    if (saved) {
      for (const line of saved.lines) {
        rows.push(this.lineRow({
          kind: 'saved', key: line.id, quantity: line.quantity, name: text(line.name), size: line.size ? text(line.size) : null,
          details: [...line.extras.map(e => extraText(text(e))), ...line.dealItems.map(d => [d.size ? text(d.size) : null, text(d.name)].filter(Boolean).join(' ')
            + (d.extras?.length ? ` (${d.extras.map(text).join(', ')})` : ''))],
          note: line.note, price: line.lineTotal ? money(line.lineTotal) : '', voided: line.isVoided, sent: line.sentToKitchen,
        }));
      }
    }

    draft.forEach((line, i) => {
      const quoted = this.quote?.lines[i];
      rows.push(this.lineRow({
        kind: 'draft', key: line.key, quantity: line.quantity, name: line.name, size: line.size, details: line.details, note: line.note,
        price: quoted ? money(quoted.lineTotal) : penceText(line.unitPence * line.quantity), isNew: !!saved,
      }));
    });

    const list = h('ol', { class: 'lines' }, rows.length ? rows : h('li', { class: 'lines-empty', text: 'Tap items to add them.' }));
    mount(this.ticketEl, head, list, this.lineTools(), this.totals(), this.actions());
    const selectedEl = list.querySelector('.line-on');
    (selectedEl ?? list.lastElementChild)?.scrollIntoView({ block: 'nearest' });
  }

  lineRow(line) {
    const on = this.selected?.kind === line.kind && this.selected.key === line.key;
    return h('li', {
      class: `line ${on ? 'line-on' : ''} ${line.voided ? 'line-void' : ''} ${line.isNew ? 'line-new' : ''}`,
      onclick: () => { if (!line.voided) { this.selected = on ? null : { kind: line.kind, key: line.key }; this.renderTicket(); } },
    },
    h('span', { class: 'line-qty', text: String(line.quantity) }),
    h('div', { class: 'line-main' },
      h('span', { class: 'line-name', text: [line.size, line.name].filter(Boolean).join(' ') }),
      line.details?.length ? h('ul', { class: 'line-details' }, line.details.map(d => h('li', { text: d }))) : null,
      line.note ? h('span', { class: 'line-note', text: line.note }) : null,
      line.voided ? h('span', { class: 'line-flag', text: 'Removed' }) : line.isNew ? h('span', { class: 'line-flag line-flag-new', text: 'New' }) : null),
    h('span', { class: 'line-price', text: line.price }));
  }

  lineTools() {
    const sel = this.selected;
    if (!sel) return null;
    if (sel.kind === 'draft') {
      const line = this.draft.lines.find(l => l.key === sel.key);
      if (!line) return null;
      return h('div', { class: 'line-tools' },
        h('button', { class: 'tool', 'aria-label': 'One fewer', onclick: () => { if (line.quantity > 1) { line.quantity--; this.draftChanged(); } }, text: '−' }),
        h('button', { class: 'tool', 'aria-label': 'One more', onclick: () => { line.quantity++; this.draftChanged(); }, text: '+' }),
        h('button', { class: 'tool', onclick: () => this.noteLine(line), text: 'Note' }),
        h('button', { class: 'tool tool-danger', onclick: () => { this.draft.lines = this.draft.lines.filter(l => l !== line); this.selected = null; this.draftChanged(); }, text: 'Remove' }));
    }

    const line = this.saved?.lines.find(l => l.id === sel.key);
    if (!line || line.isVoided || CLOSED.includes(this.saved.status)) return null;
    return h('div', { class: 'line-tools' },
      h('span', { class: 'tool-hint', text: line.sentToKitchen ? 'In the kitchen' : 'Not sent yet' }),
      h('button', { class: 'tool tool-danger', onclick: () => this.voidLine(line), text: 'Remove item' }));
  }

  async noteLine(line) {
    const note = await textDialog('Note for the kitchen', { value: line.note ?? '', required: false, suggestions: ['No onions', 'Well done', 'Extra hot', 'No salad'] });
    if (note === null) return;
    line.note = note || null;
    this.draftChanged();
  }

  totals() {
    const saved = this.saved;
    const rows = [];
    if (saved) {
      if (saved.discountAmount) rows.push(['Discount', `-${money(saved.discountAmount)}`]);
      if (saved.delivery) rows.push([`Delivery (${saved.delivery.distanceMiles} mi)`, saved.delivery.isFree ? 'Free' : money(saved.delivery.fee)]);
      rows.push(['Total', money(saved.total), 'total']);
      if (saved.paid?.minorUnits) rows.push(['Paid', money(saved.paid)]);
      if (saved.balance && saved.balance.minorUnits > 0) rows.push(['To pay', money(saved.balance), 'due']);
      if (this.draft.lines.length) rows.push(['+ New items (not sent)', this.quote ? money(this.quote.total) : '…', 'due']);
    } else {
      const q = this.quote;
      const vat = q ? q.vat.reduce((s, v) => s + v.vat.minorUnits, 0) : 0;
      const atMenu = this.draft.lines.reduce((s, l) => s + l.unitPence * l.quantity, 0);
      const fee = q?.delivery?.fee.minorUnits ?? 0;
      if (q && atMenu + fee > q.total.minorUnits) rows.push(['Offers', `-${penceText(atMenu + fee - q.total.minorUnits)}`]);
      if (q?.delivery) rows.push([`Delivery (${q.delivery.distanceMiles} mi)${q.delivery.overridden ? ' — changed' : ''}`, q.delivery.isFree ? 'Free' : money(q.delivery.fee)]);
      rows.push(['Total', q ? money(q.total) : this.draft.lines.length ? '…' : penceText(0), 'total']);
      if (q) rows.push(['incl. VAT', penceText(vat), 'small']);
    }

    const block = this.saved ? null : this.deliveryProblem();
    return h('div', { class: 'totals' },
      block ? h('p', { class: 'ticket-warning', role: 'alert', text: block }) : null,
      rows.map(([label, value, kind]) =>
        h('div', { class: `total-row ${kind ? `total-${kind}` : ''}` }, h('span', { text: label }), h('span', { text: value }))));
  }

  // ---------- Phone and delivery customers ----------

  isDeliveryMode() {
    return !this.saved && this.mode.type === 'Delivery';
  }

  /** What the hub needs to price a delivery, or null. */
  deliveryRequest() {
    if (this.mode.type !== 'Delivery' || this.draft.customer?.distanceMiles == null) return null;
    const o = this.draft.feeOverride;
    return { distanceMiles: this.draft.customer.distanceMiles, feeOverridePence: o?.pence ?? null, overrideReason: o?.reason ?? null };
  }

  /** Why this delivery cannot be sent yet, or null. */
  deliveryProblem() {
    if (!this.isDeliveryMode() || !this.draft.lines.length) return null;
    if (!this.draft.customer) return 'Add the customer’s address first.';
    const d = this.quote?.delivery;
    if (!d) return null;
    if (d.status === 'BeyondMaximumDistance') return `Too far to deliver (${d.distanceMiles} miles). A manager can still agree a fee.`;
    if (d.status === 'BelowMinimumOrder' && d.minimumOrder) {
      const food = this.quote.total.minorUnits - d.fee.minorUnits;
      return `Minimum order for delivery is ${money(d.minimumOrder)}: add ${penceText(d.minimumOrder.minorUnits - food)} more.`;
    }
    return null;
  }

  customerBox() {
    if (this.mode.source !== 'Phone') return null;
    const c = this.draft.customer;
    const o = this.draft.feeOverride;
    return h('div', { class: 'customer-box' },
      h('button', { class: `customer-btn ${c ? '' : 'customer-btn-empty'}`, onclick: () => this.editCustomer() },
        c ? contactSummary(c.contact) : this.mode.type === 'Delivery' ? '+ Add customer and address' : '+ Add customer name or phone (optional)'),
      this.mode.type === 'Delivery' && c ? h('div', { class: 'fee-row' },
        o ? h('span', { class: 'chip chip-on', text: `Fee ${penceText(o.pence)} (${o.reason}) — needs a manager` }) : null,
        o ? h('button', { class: 'chip', onclick: () => { this.draft.feeOverride = null; this.renderTicket(); this.requestQuote(); }, text: 'Normal fee' })
          : h('button', { class: 'chip', onclick: () => this.changeFee(), text: 'Change delivery fee' })) : null);
  }

  async editCustomer() {
    const result = await customerDialog({ delivery: this.mode.type === 'Delivery', initial: this.draft.customer });
    if (!result) return;
    this.draft.customer = result;
    this.renderTicket();
    this.requestQuote();
  }

  async changeFee() {
    const pence = await amountDialog('Delivery fee', { message: 'The new delivery fee. A manager will be asked to approve it.', ok: 'Next', allowZero: true });
    if (pence === null) return;
    const reason = await textDialog('Reason', { suggestions: ['Regular customer', 'Complaint last time', 'Large order', 'Just outside the area'], ok: 'Save' });
    if (!reason) return;
    this.draft.feeOverride = { pence, reason };
    this.renderTicket();
    this.requestQuote();
  }

  actions() {
    const saved = this.saved;
    const draftCount = this.draft.lines.length;
    if (!saved) {
      return h('div', { class: 'ticket-actions' },
        h('button', { class: 'btn', disabled: !draftCount, onclick: () => this.clear(), text: 'Clear' }),
        h('button', { class: 'btn btn-kitchen', disabled: !draftCount || Boolean(this.deliveryProblem()), onclick: () => this.send(), text: 'Send' }),
        h('button', { class: 'btn btn-primary btn-pay', disabled: !draftCount || !this.quote || Boolean(this.deliveryProblem()), onclick: () => this.pay() },
          'Pay ', this.quote ? money(this.quote.total) : ''));
    }

    const open = !CLOSED.includes(saved.status);
    const due = (saved.balance?.minorUnits ?? 0) > 0 || draftCount > 0;
    return h('div', { class: 'ticket-actions' },
      h('button', { class: 'btn', onclick: () => this.closeSaved(), text: draftCount ? 'Discard new' : 'Close' }),
      draftCount ? h('button', { class: 'btn btn-kitchen', onclick: () => this.send(), text: 'Send new' }) : null,
      open && due ? h('button', { class: 'btn btn-primary btn-pay', onclick: () => this.pay(), text: 'Pay' }) : null,
      open && !due ? h('button', { class: 'btn btn-primary', onclick: () => this.complete(saved), text: 'Collected' }) : null,
      h('button', { class: 'btn', onclick: () => this.savedMenu(saved), text: '⋯', 'aria-label': 'More for this order' }));
  }

  // ---------- Actions ----------

  async clear() {
    if (this.draft.lines.length > 1 && !(await confirmDialog('Clear the order?', 'All items on this order will be removed.', 'Clear'))) return;
    this.newDraft();
    this.selected = null;
    this.renderTicket();
  }

  closeSaved() {
    this.saved = null;
    this.newDraft();
    this.selected = null;
    this.renderTicket();
  }

  /** Saves the order (or the new items) on the hub and sends them to the kitchen. Returns the saved order. */
  async commit() {
    let added = false;
    if (!this.saved) {
      // Same order id and key on every retry, so a dropped connection never makes a second order.
      const body = {
        orderId: this.draft.orderId, idempotencyKey: this.draft.key, source: this.mode.source, type: this.mode.type,
        lines: this.draft.lines.map(lineRequest),
        contact: this.mode.source === 'Phone' ? this.draft.customer?.contact ?? null : null,
        delivery: this.deliveryRequest(),
      };
      const created = await withApproval(pin => api.post('/api/orders', body, { managerPin: pin }));
      if (!created) throw new ApiError(0, 'NotApproved', 'The delivery fee change was not approved. Choose "Normal fee" or ask a manager.');
      this.saved = created;
      this.draft.lines = [];
      added = true;
    } else if (this.draft.lines.length) {
      // One request id per set of new items, reused if the same items are sent again, so they are added only once.
      const lines = this.draft.lines.map(lineRequest);
      const content = JSON.stringify(lines);
      if (this.draft.addContent !== content) { this.draft.addRequestId = newId(); this.draft.addContent = content; }
      await api.post(`/api/orders/${this.saved.id}/lines`, { lines, requestId: this.draft.addRequestId });
      this.draft.lines = [];
      added = true;
    }

    if (added || this.saved.lines.some(l => !l.sentToKitchen && !l.isVoided)) {
      await api.post(`/api/orders/${this.saved.id}/send-to-kitchen`);
    }

    this.saved = await api.get(`/api/orders/${this.saved.id}`);
    this.newDraft();
    return this.saved;
  }

  async send() {
    try {
      const order = await busy(() => this.commit());
      toast(`Order ${order.number} sent to the kitchen.`, 'ok');
      this.closeSaved();
      this.refreshOpenCount();
    } catch (e) {
      this.renderTicket();
      this.showError(e);
    }
  }

  async pay() {
    let order;
    try {
      order = await busy(() => this.commit());
    } catch (e) {
      this.renderTicket();
      this.showError(e);
      return;
    }

    this.renderTicket();
    let paid = false;
    try {
      paid = await payDialog(order);
    } catch (e) {
      this.showError(e);
    }

    this.saved = await api.get(`/api/orders/${order.id}`);
    if (paid) this.closeSaved();
    else this.renderTicket();
    this.refreshOpenCount();
  }

  async complete(order) {
    try {
      await busy(() => api.post(`/api/orders/${order.id}/status`, { status: 'Completed' }));
      toast(`Order ${order.number} collected.`, 'ok');
      if (this.saved?.id === order.id) this.closeSaved();
      this.refreshOpenCount();
    } catch (e) {
      this.showError(e);
    }
  }

  async voidLine(line) {
    const reason = await textDialog(`Remove ${text(line.name)}?`, { label: 'Reason', suggestions: VOID_REASONS, ok: 'Remove' });
    if (!reason) return;
    try {
      const order = await withApproval(pin => busy(() => api.post(`/api/orders/${this.saved.id}/lines/${line.id}/void`, { reason }, { managerPin: pin })));
      if (!order) return;
      this.saved = await api.get(`/api/orders/${this.saved.id}`);
      this.selected = null;
      this.renderTicket();
      toast(line.sentToKitchen ? 'Removed. The kitchen gets a void slip.' : 'Removed.', 'ok');
    } catch (e) {
      this.showError(e);
    }
  }

  /** Reprint the receipt, or cancel the order. */
  savedMenu(order) {
    return openDialog(close => ({
      title: `Order ${order.number}`,
      class: 'dialog-narrow',
      body: h('div', { class: 'menu-list' },
        h('button', { class: 'btn menu-btn', onclick: async () => { close(); await this.printReceipt(order); }, text: 'Print receipt' }),
        order.source === 'Phone' && !CLOSED.includes(order.status) ? h('button', { class: 'btn menu-btn', onclick: async () => { close(); await this.changeSavedCustomer(order); }, text: order.type === 'Delivery' ? 'Change customer or address' : 'Change customer' }) : null,
        !CLOSED.includes(order.status) ? h('button', { class: 'btn btn-danger menu-btn', onclick: async () => { close(); await this.cancelOrder(order); }, text: 'Cancel order' }) : null),
    }));
  }

  async changeSavedCustomer(order) {
    const delivery = order.type === 'Delivery';
    const result = await customerDialog({ delivery, initial: { contact: order.contact, distanceMiles: order.delivery?.distanceMiles ?? null } });
    if (!result) return;
    try {
      await busy(async () => {
        await api.post(`/api/orders/${order.id}/contact`, result.contact);
        if (delivery && result.distanceMiles !== order.delivery?.distanceMiles) {
          await api.post(`/api/orders/${order.id}/delivery`, { distanceMiles: result.distanceMiles });
        }
      });
      this.saved = await api.get(`/api/orders/${order.id}`);
      this.renderTicket();
      toast('Customer details saved. Reprint the receipt for the driver if it has already printed.', 'ok', 6000);
    } catch (e) {
      this.showError(e);
    }
  }

  async printReceipt(order) {
    try {
      await busy(() => api.post(`/api/orders/${order.id}/print-receipt`));
      toast('Receipt printing.', 'ok');
    } catch (e) {
      this.showError(e);
    }
  }

  async cancelOrder(order) {
    if ((order.paid?.minorUnits ?? 0) > 0) {
      await alertDialog('Refund first', `${money(order.paid)} has been paid on this order. Refund it before cancelling.`);
      return;
    }

    const reason = await textDialog(`Cancel order ${order.number}?`, { label: 'Reason', suggestions: ['Customer left', 'Duplicate order', 'Test order'], ok: 'Cancel order' });
    if (!reason) return;
    try {
      const done = await withApproval(pin => busy(() => api.post(`/api/orders/${order.id}/status`, { status: 'Cancelled', reason }, { managerPin: pin })));
      if (!done) return;
      toast(`Order ${order.number} cancelled.`, 'ok');
      this.closeSaved();
      this.refreshOpenCount();
    } catch (e) {
      this.showError(e);
    }
  }

  /** Today's orders; tap one to open it. */
  async showOrders() {
    let orders;
    try {
      orders = await busy(() => api.get('/api/orders'));
    } catch (e) {
      this.showError(e);
      return;
    }

    let showAll = false;
    const picked = await openDialog(close => {
      const list = h('div', { class: 'orders' });
      const filter = h('div', { class: 'seg seg-small' });
      const render = () => {
        mount(filter, [false, true].map(all => h('button', {
          class: `seg-btn ${showAll === all ? 'seg-on' : ''}`, onclick: () => { showAll = all; render(); }, text: all ? 'All today' : 'Open',
        })));
        const shown = orders.filter(o => showAll || !CLOSED.includes(o.status)).sort((a, b) => b.number.localeCompare(a.number));
        mount(list, shown.length ? shown.map(o => {
          const due = o.balance?.minorUnits ?? 0;
          return h('div', { class: 'order-row' },
            h('button', { class: 'order-open', onclick: () => close({ open: o }) },
              h('span', { class: 'order-no', text: o.number }),
              h('span', { class: 'order-meta', text: [timeText(o.createdAt), modeLabel(o.source, o.type), o.contact?.name, o.contact?.address?.postcode].filter(Boolean).join(' · ') }),
              h('span', { class: `status status-${o.status.toLowerCase()}`, text: o.status }),
              h('span', { class: 'order-total', text: money(o.total) }),
              h('span', { class: `paid-badge ${due > 0 ? 'paid-due' : 'paid-ok'}`, text: due > 0 ? `Due ${penceText(due)}` : 'Paid' })),
            !CLOSED.includes(o.status) && due <= 0 ? h('button', { class: 'btn btn-small', onclick: () => close({ complete: o }), text: 'Collected' }) : null);
        }) : h('p', { class: 'empty-note', text: showAll ? 'No orders yet today.' : 'No open orders.' }));
      };
      render();
      return { title: 'Today’s orders', wide: true, body: [filter, list] };
    });

    if (picked?.open) {
      if (this.draft.lines.length && !this.saved && !(await confirmDialog('Leave this order?', 'The items not yet sent will be removed.', 'Leave'))) return;
      this.saved = await api.get(`/api/orders/${picked.open.id}`);
      this.newDraft();
      this.selected = null;
      this.renderTicket();
    } else if (picked?.complete) {
      await this.complete(picked.complete);
    }
  }

  showError(e) {
    if (e instanceof ApiError && e.problems?.length) {
      alertDialog('Please check the order', e.problems.map(p => p.message).join('\n'));
    } else {
      toast(e?.message ?? 'Something went wrong.', 'error', 6000);
    }
  }
}
