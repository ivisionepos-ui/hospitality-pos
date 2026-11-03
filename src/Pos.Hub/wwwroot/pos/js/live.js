// Live updates from the hub (orders, menu, payments, printer and backup alerts).
// A small client for SignalR's JSON protocol over a WebSocket. Every event has a number; after a drop
// the screen asks the hub for anything it missed, so nothing is lost.
import { api } from './api.js';

const RS = '\u001e'; // SignalR message separator

export class LiveConnection {
  /**
   * @param {(event: object) => void} onEvent called once per event, in order
   * @param {(connected: boolean) => void} onStatus called when the connection goes up or down
   */
  constructor(onEvent, onStatus) {
    this.onEvent = onEvent;
    this.onStatus = onStatus;
    this.lastSeq = null;
    this.stopped = false;
    this.retry = 0;
    this.socket = null;
    this.pinger = null;
  }

  start() {
    this.stopped = false;
    this.connect();
  }

  stop() {
    this.stopped = true;
    clearInterval(this.pinger);
    this.socket?.close();
  }

  async connect() {
    if (this.stopped) return;
    try {
      const ticket = await api.post('/api/live/ticket');
      if (this.lastSeq === null) this.lastSeq = ticket.lastSeq;
      const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
      const socket = new WebSocket(`${scheme}://${location.host}/live?access_token=${encodeURIComponent(ticket.ticket)}`);
      this.socket = socket;
      let buffer = '';
      socket.onopen = () => socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
      socket.onmessage = message => {
        buffer += message.data;
        const parts = buffer.split(RS);
        buffer = parts.pop();
        for (const part of parts) this.handle(JSON.parse(part));
      };
      socket.onclose = () => this.dropped();
      socket.onerror = () => socket.close();
    } catch {
      this.dropped();
    }
  }

  async handle(message) {
    if (message.error) {
      this.socket.close();
      return;
    }

    if (message.type === undefined) {
      // Handshake accepted: connected. Fetch anything missed while away.
      this.retry = 0;
      this.onStatus(true);
      clearInterval(this.pinger);
      this.pinger = setInterval(() => this.socket?.readyState === 1 && this.socket.send(JSON.stringify({ type: 6 }) + RS), 15000);
      await this.catchUp();
    } else if (message.type === 1 && message.target === 'event') {
      const event = message.arguments[0];
      if (event.seq <= this.lastSeq) return;
      if (event.seq > this.lastSeq + 1) await this.catchUp(); // a gap: fetch the missing ones first
      if (event.seq === this.lastSeq + 1) this.deliver(event);
    } else if (message.type === 7) {
      this.socket.close();
    }
  }

  deliver(event) {
    this.lastSeq = event.seq;
    try { this.onEvent(event); } catch (e) { console.error(e); }
  }

  async catchUp() {
    for (let more = true; more;) {
      const result = await api.get(`/api/live/events?since=${this.lastSeq}`);
      for (const event of result.events) if (event.seq > this.lastSeq) this.deliver(event);
      more = result.more;
    }
  }

  dropped() {
    clearInterval(this.pinger);
    this.onStatus(false);
    if (this.stopped) return;
    const wait = Math.min(1000 * 2 ** this.retry, 15000);
    this.retry++;
    setTimeout(() => this.connect(), wait);
  }
}
