// Sends what the till shows to the customer display that follows this till (More → Customer displays).
// The display is a nicety: if sending fails the till carries on and the next change sends everything again.
import { api } from './api.js';

const WAIT_MS = 120; // several quick changes (tapping +, the quote arriving) go as one

class DisplayFeed {
  constructor() {
    this.last = null;    // the order as last shown (lines and totals), kept for the payment screens
    this.sent = '';
    this.timer = null;
  }

  /** The order on the till changed (called each time the order panel is drawn). */
  order(state) {
    this.last = state;
    this.queue(state);
  }

  /** Taking payment: the order with what is still to pay, and the change from a cash payment if any. */
  pay({ toPay, totals, change = null, orderNumber = null }) {
    this.send({ ...(this.last ?? empty()), stage: 'pay', toPay, totals: totals ?? this.last?.totals ?? [], change, orderNumber: orderNumber ?? this.last?.orderNumber ?? null });
  }

  /** Fully paid: "Thank you" with the order number and the change. */
  thanks({ change = null, orderNumber = null }) {
    this.send({ ...empty(), stage: 'thanks', orderNumber: orderNumber ?? this.last?.orderNumber ?? null, change });
  }

  /** Back to the welcome screen (e.g. the till logs out). */
  clear() {
    this.last = null;
    return this.send(empty());
  }

  queue(state) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.send(state), WAIT_MS);
  }

  async send(state) {
    clearTimeout(this.timer);
    const body = JSON.stringify(state);
    if (body === this.sent) return;
    this.sent = body;
    try {
      await api.post('/api/display/state', state);
    } catch {
      this.sent = ''; // try again with the next change
    }
  }
}

function empty() {
  return { stage: 'idle', orderNumber: null, orderType: null, lines: [], totals: [], toPay: null, change: null };
}

export const displayFeed = new DisplayFeed();
