// Pop-up panels, number keypads, and the common questions (confirm, reason, manager PIN, amount).
import { h, mount, penceText } from './dom.js';

/** The next stacking level: whatever opens last (a dialog or the working cover) goes on top. */
function nextLayer() {
  const levels = [...document.querySelectorAll('.dialog-layer, .busy')].map(el => Number(el.style.zIndex) || 0);
  return Math.max(10, ...levels) + 1;
}

/**
 * Opens a dialog. build(close) returns { title, body, actions?, wide? }. Returns a promise of the value passed to close().
 * Tapping outside or "Cancel" closes with null.
 */
export function openDialog(build) {
  return new Promise(resolve => {
    const host = document.getElementById('dialogs');
    const layer = h('div', { class: 'dialog-layer' });
    layer.style.zIndex = String(nextLayer());
    let done = false;
    const close = value => {
      if (done) return;
      done = true;
      layer.remove();
      document.removeEventListener('keydown', onKey);
      resolve(value ?? null);
    };
    const onKey = e => { if (e.key === 'Escape') close(null); };
    const spec = build(close);
    const panel = h('div', { class: `dialog ${spec.wide ? 'dialog-wide' : ''} ${spec.class ?? ''}`, role: 'dialog', 'aria-modal': 'true', 'aria-label': spec.title },
      h('div', { class: 'dialog-head' },
        h('h2', { text: spec.title }),
        spec.noCancel ? null : h('button', { class: 'icon-btn', 'aria-label': 'Close', onclick: () => close(null), text: '✕' })),
      h('div', { class: 'dialog-body' }, spec.body),
      spec.actions ? h('div', { class: 'dialog-actions' }, spec.actions) : null);
    layer.appendChild(panel);
    layer.addEventListener('pointerdown', e => { if (e.target === layer && !spec.noCancel) close(null); });
    document.addEventListener('keydown', onKey);
    host.appendChild(layer);
    spec.focus?.focus();
  });
}

/** A message with an OK button. */
export function alertDialog(title, message) {
  return openDialog(close => ({
    title,
    body: h('p', { class: 'dialog-message', text: message }),
    actions: [h('button', { class: 'btn btn-primary', onclick: () => close(true), text: 'OK' })],
  }));
}

/** Yes/no question. Resolves true or false. */
export async function confirmDialog(title, message, yes = 'Yes', danger = false) {
  return (await openDialog(close => ({
    title,
    body: h('p', { class: 'dialog-message', text: message }),
    actions: [
      h('button', { class: 'btn', onclick: () => close(false), text: 'No' }),
      h('button', { class: `btn ${danger ? 'btn-danger' : 'btn-primary'}`, onclick: () => close(true), text: yes }),
    ],
  }))) === true;
}

/** Asks for a short text, with quick-pick suggestions. Resolves the text or null. */
export function textDialog(title, { label = '', value = '', suggestions = [], required = true, ok = 'OK', maxLength = 120 } = {}) {
  return openDialog(close => {
    const input = h('input', { class: 'text-input', type: 'text', value, maxlength: maxLength, 'aria-label': label || title, autocomplete: 'off' });
    const submit = () => {
      const v = input.value.trim();
      if (required && !v) { input.focus(); return; }
      close(v);
    };
    input.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
    return {
      title,
      focus: input,
      body: [
        label ? h('label', { class: 'field-label', text: label }) : null,
        input,
        suggestions.length ? h('div', { class: 'chips' }, suggestions.map(s =>
          h('button', { class: 'chip', onclick: () => { input.value = s; submit(); }, text: s }))) : null,
      ],
      actions: [
        h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: submit, text: ok }),
      ],
    };
  });
}

/**
 * A number keypad bound to a value.
 * mode "money": digits fill from the right like a till (1,2,5,0 → £12.50). mode "pin": digits shown as dots.
 * mode "count": whole numbers.
 */
export function keypad({ mode = 'money', initial = 0, maxDigits = 7, onChange = () => {}, onEnter = null, enterText = 'OK' }) {
  let digits = mode === 'money' ? (initial ? String(initial) : '') : '';
  // A pre-filled amount is replaced by the first digit typed, like a selected field.
  let fresh = Boolean(digits);
  const display = h('div', { class: `keypad-display keypad-${mode}`, 'aria-live': 'polite' });
  const value = () => (mode === 'pin' ? digits : Number(digits || '0'));
  const show = () => {
    if (mode === 'money') display.textContent = penceText(Number(digits || '0'));
    else if (mode === 'pin') display.textContent = digits ? '•'.repeat(digits.length) : ' ';
    else display.textContent = digits || '0';
    onChange(value());
  };
  const press = key => {
    if (fresh && /^\d+$/.test(key)) digits = '';
    fresh = false;
    if (key === 'C') digits = '';
    else if (key === '⌫') digits = digits.slice(0, -1);
    else if (key === '00') { if (digits && digits.length + 2 <= maxDigits) digits += '00'; }
    else if (digits.length < maxDigits) digits = digits === '0' ? key : digits + key;
    show();
  };
  const keys = ['1', '2', '3', '4', '5', '6', '7', '8', '9', mode === 'money' ? '00' : 'C', '0', '⌫'];
  const grid = h('div', { class: 'keypad-grid' }, keys.map(k =>
    h('button', { class: `key ${k === 'C' || k === '⌫' ? 'key-fn' : ''}`, onclick: () => press(k), text: k, 'aria-label': k === '⌫' ? 'Delete' : k })));
  const onKey = e => {
    if (!document.body.contains(display)) { document.removeEventListener('keydown', onKey); return; }
    // Only the keypad on top (the newest dialog, or the main screen when no dialog is open) takes typing.
    const layers = document.querySelectorAll('.dialog-layer');
    const top = layers.length ? layers[layers.length - 1] : null;
    if (display.closest('.dialog-layer') !== top) return;
    if (e.target instanceof HTMLInputElement) return;
    if (/^[0-9]$/.test(e.key)) press(e.key);
    else if (e.key === 'Backspace') press('⌫');
    else if (e.key === 'Enter' && onEnter) onEnter(value());
  };
  document.addEventListener('keydown', onKey);
  show();
  const el = h('div', { class: 'keypad' }, display, grid,
    onEnter ? h('button', { class: 'btn btn-primary keypad-enter', onclick: () => onEnter(value()), text: enterText }) : null);
  return {
    el,
    value,
    set(v) { digits = v ? String(v) : ''; fresh = Boolean(digits); show(); },
    clear() { digits = ''; show(); },
  };
}

/** Asks for an amount of money in pence. Resolves the pence or null. */
export function amountDialog(title, { initial = 0, message = '', ok = 'OK', allowZero = false } = {}) {
  return openDialog(close => {
    const pad = keypad({
      mode: 'money', initial,
      onEnter: v => { if (allowZero || v > 0) close(v); },
      enterText: ok,
    });
    return { title, body: [message ? h('p', { class: 'dialog-message', text: message }) : null, pad.el] };
  });
}

/** Asks a manager to approve with their PIN. Resolves the PIN or null. */
export function managerPinDialog(reason) {
  return openDialog(close => {
    const pad = keypad({ mode: 'pin', maxDigits: 8, onEnter: v => { if (v.length >= 4) close(v); }, enterText: 'Approve' });
    return {
      title: 'Manager approval',
      class: 'dialog-narrow',
      body: [h('p', { class: 'dialog-message', text: `${reason} Manager, enter your PIN.` }), pad.el],
    };
  });
}

/** A screen-wide "working" cover while a request is in flight. */
export async function busy(work) {
  const cover = h('div', { class: 'busy' }, h('div', { class: 'spinner', 'aria-label': 'Working' }));
  cover.style.zIndex = String(nextLayer());
  document.body.appendChild(cover);
  try {
    return await work();
  } finally {
    cover.remove();
  }
}

export { mount };
