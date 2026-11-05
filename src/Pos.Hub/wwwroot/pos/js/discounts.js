// Discounts off an order, refunds of payments, and the owner's discount limits.
import { api, ApiError } from './api.js';
import { h, mount, money, newId, penceText, timeText, toast } from './dom.js';
import { alertDialog, amountDialog, busy, confirmDialog, keypad, openDialog, textDialog } from './dialogs.js';
import { ensureShift } from './pay.js';

const QUICK_PERCENTS = [5, 10, 15, 20, 25, 50, 100];
const DISCOUNT_REASONS = ['Regular customer', 'Staff meal', 'Complaint', 'Late order', 'Promotion'];
const REFUND_REASONS = ['Complaint', 'Wrong order', 'Item not available', 'Charged twice', 'Overcharged'];
const METHOD = { Cash: 'Cash', Card: 'Card', DeliveryApp: 'Delivery app', Online: 'Online' };

/** "Without a manager: up to 10% or £5.00". */
export function limitsText(limits) {
  if (!limits) return '';
  return limits.staffMaxPercent === 0 && limits.staffMaxPence === 0
    ? 'Every discount needs a manager.'
    : `Without a manager: up to ${limits.staffMaxPercent}% and ${penceText(limits.staffMaxPence)} off.`;
}

/**
 * Asks for a discount. order: the saved order (for the total and any discount already on it).
 * Resolves { type, value, reason }, { remove: true }, or null.
 */
export function discountDialog(order, limits) {
  const goods = order.goodsTotal.minorUnits;
  return openDialog(close => {
    let type = 'Percent';
    let value = 0;
    const preview = h('p', { class: 'discount-preview', 'aria-live': 'polite' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    const modes = h('div', { class: 'seg', role: 'radiogroup', 'aria-label': 'Discount type' });
    const quick = h('div', { class: 'chips' });
    const padHost = h('div');
    const reason = h('input', { class: 'text-input', type: 'text', maxlength: 60, autocomplete: 'off', 'aria-label': 'Reason', placeholder: 'Reason (shown on the receipt)' });
    const off = () => (type === 'Percent' ? Math.round(goods * value / 100) : Math.min(value, goods));
    const showPreview = () => {
      problem.textContent = '';
      preview.textContent = value
        ? `${type === 'Percent' ? `${value}% off` : `${penceText(value)} off`}: −${penceText(off())}, items now ${penceText(goods - off())}`
        : `Items ${penceText(goods)}`;
      mount(quick, type === 'Percent' ? QUICK_PERCENTS.map(p => h('button', {
        type: 'button', class: `chip ${value === p ? 'chip-on' : ''}`, 'aria-pressed': String(value === p),
        onclick: () => { value = p; pad.set(p); showPreview(); }, text: `${p}%`,
      })) : null);
    };
    let pad;
    const renderPad = () => {
      pad = keypad({ mode: type === 'Percent' ? 'count' : 'money', maxDigits: type === 'Percent' ? 3 : 6, onChange: v => { value = v; showPreview(); } });
      mount(padHost, pad.el);
    };
    const renderModes = () => mount(modes, [['Percent', '% off'], ['Amount', '£ off']].map(([id, label]) => h('button', {
      type: 'button', class: `seg-btn ${type === id ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(type === id),
      onclick: () => { if (type === id) return; type = id; value = 0; renderModes(); renderPad(); showPreview(); }, text: label,
    })));
    renderModes();
    renderPad();
    showPreview();
    const apply = () => {
      if (!value) { problem.textContent = type === 'Percent' ? 'Choose or type the percentage.' : 'Type the amount off.'; return; }
      if (type === 'Percent' && value > 100) { problem.textContent = 'The percentage must be 1 to 100.'; return; }
      if (!reason.value.trim()) { problem.textContent = 'Choose or type a reason.'; reason.focus(); return; }
      close({ type, value, reason: reason.value.trim() });
    };
    return {
      title: `Discount · order ${order.number}`,
      wide: true,
      body: h('div', { class: 'discount-grid' },
        h('div', { class: 'discount-left' },
          modes, quick, padHost),
        h('div', { class: 'discount-right' },
          order.discountAmount ? h('p', { class: 'dialog-message', text: `Now: −${money(order.discountAmount)} (${order.discountReason ?? 'discount'}). A new discount replaces it.` }) : null,
          preview,
          h('span', { class: 'field-label', text: 'Reason' }),
          h('div', { class: 'chips' }, DISCOUNT_REASONS.map(r => h('button', { type: 'button', class: 'chip', onclick: () => { reason.value = r; problem.textContent = ''; }, text: r }))),
          reason,
          h('p', { class: 'field-hint', text: limitsText(limits) }),
          problem)),
      actions: [
        order.discountAmount ? h('button', { class: 'btn btn-danger-soft', onclick: () => close({ remove: true }), text: 'Remove discount' }) : null,
        h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: apply, text: 'Apply discount' }),
      ],
    };
  });
}

/** How much of each payment can still be refunded. */
function refundable(payments) {
  return payments.filter(p => !p.refundOf).map(p => ({
    payment: p,
    left: p.amount.minorUnits + payments.filter(r => r.refundOf === p.id).reduce((s, r) => s + r.amount.minorUnits, 0),
  }));
}

/**
 * Refunds all or part of a payment on an order (manager approval, reason). Resolves true when a refund was made.
 * withApproval: the till's "ask for a manager PIN and retry" helper.
 */
export async function refundDialog(order, withApproval) {
  const ledger = await busy(() => api.get(`/api/orders/${order.id}/payments`));
  const rows = refundable(ledger.payments);
  if (!rows.some(r => r.left > 0)) { await alertDialog('Nothing to refund', 'Nothing has been paid on this order, or it has all been refunded.'); return false; }
  const chosen = await openDialog(close => ({
    title: `Refund · order ${order.number}`,
    body: [
      h('p', { class: 'dialog-message', text: 'Money goes back the way it was paid. Choose the payment:' }),
      h('div', { class: 'staff-list' }, rows.map(r => h('div', { class: 'staff-row', 'aria-label': `${METHOD[r.payment.method] ?? r.payment.method} ${money(r.payment.amount)}` },
        h('div', { class: 'staff-who' },
          h('strong', { text: `${METHOD[r.payment.method] ?? r.payment.method} ${money(r.payment.amount)}` }),
          h('span', { class: 'field-hint', text: [timeText(r.payment.at), r.payment.reference ? `ref ${r.payment.reference}` : null,
            r.left < r.payment.amount.minorUnits ? `${penceText(r.payment.amount.minorUnits - r.left)} already refunded` : null].filter(Boolean).join(' · ') })),
        h('button', { class: 'btn btn-small', disabled: r.left <= 0, onclick: () => close(r), text: r.left > 0 ? `Refund up to ${penceText(r.left)}` : 'Refunded' })))),
    ],
  }));
  if (!chosen) return false;

  const method = METHOD[chosen.payment.method] ?? chosen.payment.method;
  if (chosen.payment.method === 'DeliveryApp' || chosen.payment.method === 'Online') {
    await alertDialog('Refund through the app', `This was paid ${method === 'Online' ? 'online' : 'to the delivery app'}. Refund it there; the till cannot send money back to it.`);
    return false;
  }

  const amount = await amountDialog(`Refund to ${method.toLowerCase()}`, { initial: chosen.left, message: `Up to ${penceText(chosen.left)}. Type a smaller amount for a part refund.`, ok: 'Next' });
  if (!amount) return false;
  if (amount > chosen.left) { await alertDialog('Too much', `Only ${penceText(chosen.left)} of this payment can be refunded.`); return false; }
  const reason = await textDialog('Reason for the refund', { suggestions: REFUND_REASONS, ok: 'Refund' });
  if (!reason) return false;
  if (!(await ensureShift())) return false; // a refund comes out of this till's shift (cash from its drawer)

  const refundId = newId(); // the same id if it is sent again, so it is refunded only once
  const done = await withApproval(pin => busy(() => api.post(`/api/orders/${order.id}/payments/${chosen.payment.id}/refund`, { amountPence: amount, reason, refundId }, { managerPin: pin })));
  if (!done) return false;

  await alertDialog(
    chosen.payment.method === 'Cash' ? `Give ${penceText(amount)} cash` : `Now refund ${penceText(amount)} on the card machine`,
    chosen.payment.method === 'Cash'
      ? `Take ${penceText(amount)} from the drawer and give it to the customer. The till has recorded it.`
      : `The till has recorded the refund. Do the refund on the card machine to the customer's card, for ${penceText(amount)}.`);
  if (await confirmDialog('Print a receipt?', 'The receipt shows the refund.', 'Print')) {
    try { await busy(() => api.post(`/api/orders/${order.id}/print-receipt`)); toast('Receipt printing.', 'ok'); } catch (e) { toast(e.message, 'error'); }
  }
  return true;
}

/** The owner's discount limits (More → Discount limits). */
export async function discountLimitsDialog() {
  const current = await busy(() => api.get('/api/settings/discounts'));
  await openDialog(close => {
    const percent = h('input', { class: 'text-input', type: 'number', min: 0, max: 100, step: 1, value: current.staffMaxPercent, 'aria-label': 'Largest % off without a manager' });
    const pounds = h('input', { class: 'text-input', type: 'number', min: 0, max: 1000, step: 0.5, value: (current.staffMaxPence / 100).toFixed(2), 'aria-label': 'Largest £ off without a manager' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    const save = async () => {
      const body = { staffMaxPercent: Number(percent.value), staffMaxPence: Math.round(Number(pounds.value) * 100) };
      if (!Number.isInteger(body.staffMaxPercent) || !Number.isFinite(body.staffMaxPence)) { problem.textContent = 'Enter whole percent and pounds.'; return; }
      try {
        const saved = await busy(() => api.put('/api/settings/discounts', body));
        toast(`Saved. ${limitsText(saved)}`, 'ok', 5000);
        close(true);
      } catch (e) {
        problem.textContent = e instanceof ApiError ? e.message : 'Could not save.';
      }
    };
    return {
      title: 'Discount limits',
      body: [
        h('p', { class: 'dialog-message', text: 'Cashiers and waiters may give discounts up to these limits on their own. Above either one, a manager enters their PIN. Managers and owners can always give discounts. Every discount needs a reason and is on the receipt, the order history and the Z report.' }),
        h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Largest % off without a manager (0 = always ask)' }), percent),
        h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Largest £ off without a manager (0 = always ask)' }), pounds),
        problem,
      ],
      actions: [h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }), h('button', { class: 'btn btn-primary', onclick: save, text: 'Save' })],
    };
  });
}
