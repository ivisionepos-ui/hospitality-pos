// The customer display: faces the customer on the till's second screen or a tablet on the counter.
// It shows the order as it is rung up, the total, what is left to pay and the change, "Thank you" after paying,
// and between orders a welcome screen with the owner's messages. It only shows what its till sends.
import { api, ApiError } from './api.js';
import { h, mount } from './dom.js';

const THANKS_FOR_MS = 10000;  // "Thank you" stays this long before the welcome screen
const MESSAGE_EVERY_MS = 6000; // the owner's messages take turns

export class CustomerDisplay {
  constructor(root) {
    this.root = root;
    this.view = null;        // shop name, welcome, messages, till name
    this.state = { stage: 'idle', lines: [], totals: [] };
    this.thanksUntil = 0;
    this.pendingIdle = null;
    this.messageIndex = 0;
    this.live = false;
  }

  async start() {
    document.body.classList.add('cd-body');
    await this.reload();
    clearInterval(this.ticker);
    this.ticker = setInterval(() => this.tick(), 1000);
  }

  /** Fetches the shop, the owner's messages and what the till shows now (at start, after a reconnection, after the owner saves). */
  async reload() {
    try {
      this.view = await api.get('/api/display');
      this.show(this.view.state);
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) {
        mount(this.root, h('div', { class: 'cd cd-idle' }, h('p', { class: 'cd-welcome', text: 'This device is not a customer display.' })));
        return;
      }

      this.render(); // offline: keep showing what we had
    }
  }

  setLive(connected) {
    const was = this.live;
    this.live = connected;
    if (connected && !was) this.reload(); // anything sent while away
    this.root.querySelector('.cd-offline')?.toggleAttribute('hidden', connected);
  }

  /** A new state from the till. "Thank you" is kept on screen for a while even when the till starts the next order empty. */
  show(state) {
    if (!state) return;
    clearTimeout(this.pendingIdle);
    if (state.stage === 'thanks') this.thanksUntil = Date.now() + THANKS_FOR_MS;
    if (state.stage === 'idle' && this.state.stage === 'thanks' && Date.now() < this.thanksUntil) {
      this.pendingIdle = setTimeout(() => this.show(state), this.thanksUntil - Date.now());
      return;
    }

    this.state = state;
    this.render();
  }

  tick() {
    const clock = this.root.querySelector('.cd-clock');
    if (clock) clock.textContent = new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    const messages = this.view?.messages ?? [];
    if (this.state.stage === 'idle' && messages.length > 1 && Date.now() - (this.messageAt ?? 0) >= MESSAGE_EVERY_MS) {
      this.messageAt = Date.now();
      this.messageIndex = (this.messageIndex + 1) % messages.length;
      const el = this.root.querySelector('.cd-message');
      if (el) { el.textContent = messages[this.messageIndex]; el.classList.remove('cd-fade'); void el.offsetWidth; el.classList.add('cd-fade'); }
    }
  }

  render() {
    const s = this.state;
    const offline = h('span', { class: 'cd-offline', hidden: this.live, text: 'Reconnecting…' });
    if (s.stage === 'thanks') {
      mount(this.root, h('div', { class: 'cd cd-thanks', 'aria-live': 'polite' },
        h('p', { class: 'cd-thanks-big', text: 'Thank you!' }),
        s.orderNumber ? h('p', { class: 'cd-thanks-number' }, 'Your order number ', h('strong', { text: s.orderNumber })) : null,
        s.change ? h('p', { class: 'cd-thanks-change' }, h('span', { text: 'Your change' }), h('strong', { text: s.change })) : null,
        offline));
      return;
    }

    if (s.stage === 'idle' || !s.lines?.length) {
      this.shownCount = 0;
      const messages = this.view?.messages ?? [];
      mount(this.root, h('div', { class: 'cd cd-idle' },
        h('p', { class: 'cd-shop', text: this.view?.shopName ?? '' }),
        h('p', { class: 'cd-welcome', text: this.view?.welcome ?? 'Welcome' }),
        messages.length ? h('p', { class: 'cd-message cd-fade', text: messages[this.messageIndex % messages.length] }) : null,
        h('p', { class: 'cd-clock' }),
        this.view && !this.view.tillName ? h('p', { class: 'cd-note', text: 'Not linked to a till yet. Owner: More → Customer displays on a till.' }) : null,
        offline));
      this.tick();
      return;
    }

    // The order: items on the left (newest at the bottom, highlighted), totals on the right.
    const added = s.lines.length > (this.shownCount ?? 0); // only a new item slides in, not every change
    this.shownCount = s.lines.length;
    const lines = h('ol', { class: 'cd-lines' }, s.lines.map((l, i) => h('li', {
      class: `cd-line ${i === s.lines.length - 1 ? `cd-line-new ${added ? 'cd-line-added' : ''}` : ''} ${l.removed ? 'cd-line-removed' : ''}`,
    },
    h('span', { class: 'cd-qty', text: String(l.quantity) }),
    h('span', { class: 'cd-name' }, l.name, l.details?.length ? h('small', { text: l.details.join(', ') }) : null),
    h('span', { class: 'cd-price', text: l.removed ? 'Removed' : l.price ?? '' }))));

    const paying = s.stage === 'pay';
    mount(this.root, h('div', { class: `cd cd-order ${paying ? 'cd-paying' : ''}` },
      h('section', { class: 'cd-left', 'aria-label': 'Your order' },
        h('header', { class: 'cd-head' }, h('span', { text: this.view?.shopName ?? '' }), h('span', { text: [s.orderType, s.orderNumber ? `Order ${s.orderNumber}` : null].filter(Boolean).join(' · ') })),
        lines),
      h('section', { class: 'cd-right', 'aria-label': 'Total', 'aria-live': 'polite' },
        h('dl', { class: 'cd-totals' }, (s.totals ?? []).map(t => [
          h('dt', { class: t.kind ? `cd-t-${t.kind}` : null, text: t.label }),
          h('dd', { class: t.kind ? `cd-t-${t.kind}` : null, text: t.value })])),
        paying && s.toPay ? h('div', { class: 'cd-topay' }, h('span', { text: 'To pay' }), h('strong', { text: s.toPay })) : null,
        s.change ? h('div', { class: 'cd-change' }, h('span', { text: 'Your change' }), h('strong', { text: s.change })) : null),
      offline));
    lines.lastElementChild?.scrollIntoView({ block: 'end' });
  }
}
