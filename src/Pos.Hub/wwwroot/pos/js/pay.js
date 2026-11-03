// Taking payment: cash (change worked out) or card on the Worldpay terminal, part payments, safe retries.
import { api, ApiError } from './api.js';
import { h, mount, money, newId, penceText, toast } from './dom.js';
import { amountDialog, busy, keypad, openDialog } from './dialogs.js';

/** Makes sure this till has an open shift, offering to open one. Resolves true when there is one. */
export async function ensureShift() {
  const shift = await api.get('/api/shifts/current');
  if (shift) return true;
  const float = await amountDialog('Open the till', {
    message: 'Start the shift: count the cash in the drawer (the float) and enter it.',
    ok: 'Open till', allowZero: true,
  });
  if (float === null) return false;
  await busy(() => api.post('/api/shifts/open', { openingFloatPence: float }));
  toast(`Till opened with a ${penceText(float)} float.`, 'ok');
  return true;
}

/** Shows the change to give, big, until tapped. */
function changeDialog(change) {
  return openDialog(close => ({
    title: 'Give change',
    class: 'dialog-change',
    body: h('button', { class: 'change-big', onclick: () => close(true) },
      h('span', { class: 'change-label', text: 'Change' }),
      h('span', { class: 'change-amount', text: penceText(change) })),
    actions: [h('button', { class: 'btn btn-primary btn-big', onclick: () => close(true), text: 'Done' })],
  }));
}

/**
 * Takes payment for a saved order until it is paid or the cashier closes the panel.
 * Resolves true when the order is fully paid.
 */
export async function payDialog(order) {
  if (!(await ensureShift())) return false;
  let ledger = await api.get(`/api/orders/${order.id}/payments`);
  if (ledger.balance.minorUnits <= 0) return true;

  return openDialog(close => {
    let method = 'cash';
    // One id per payment attempt, kept until the hub confirms, so a retry never charges twice.
    let pendingId = null;
    let pendingKey = '';
    const summary = h('div', { class: 'pay-summary' });
    const panel = h('div', { class: 'pay-panel' });
    const reference = h('input', { class: 'text-input', type: 'text', maxlength: 40, placeholder: 'Optional', 'aria-label': 'Terminal receipt number' });

    const balance = () => ledger.balance.minorUnits;

    const showSummary = () => mount(summary,
      h('div', { class: 'pay-row' }, h('span', { text: 'Total' }), h('strong', { text: money(ledger.total) })),
      ledger.paid.minorUnits ? h('div', { class: 'pay-row' }, h('span', { text: 'Paid' }), h('span', { text: money(ledger.paid) })) : null,
      h('div', { class: 'pay-row pay-due' }, h('span', { text: 'To pay' }), h('strong', { text: money(ledger.balance) })),
      ledger.payments.length ? h('ul', { class: 'pay-list' }, ledger.payments.map(p =>
        h('li', {}, h('span', { text: `${p.method}${p.reference ? ` #${p.reference}` : ''}` }), h('span', { text: money(p.amount) })))) : null);

    const take = async (kind, body) => {
      const key = `${kind}:${JSON.stringify(body)}`;
      if (key !== pendingKey) { pendingId = newId(); pendingKey = key; }
      try {
        const result = await busy(() => api.post(`/api/orders/${order.id}/payments/${kind}`, { ...body, paymentId: pendingId }));
        pendingId = null;
        pendingKey = '';
        ledger = result.order;
        const change = result.payment.change?.minorUnits ?? 0;
        if (change > 0) await changeDialog(change);
        if (ledger.balance.minorUnits <= 0) {
          toast('Paid. Receipt printing.', 'ok');
          close(true);
          return;
        }

        toast(`${penceText(result.payment.amount.minorUnits)} taken. ${penceText(balance())} still to pay.`, 'info');
        render();
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Payment failed.', 'error', 6000);
      }
    };

    const render = () => {
      showSummary();
      const tabs = h('div', { class: 'seg' },
        ['cash', 'card'].map(m => h('button', {
          class: `seg-btn ${method === m ? 'seg-on' : ''}`, 'aria-pressed': String(method === m),
          onclick: () => { method = m; render(); }, text: m === 'cash' ? 'Cash' : 'Card',
        })));

      if (method === 'cash') {
        const action = h('button', { class: 'btn btn-primary btn-big' });
        const pad = keypad({
          mode: 'money',
          onChange: v => {
            action.textContent = v ? `Take cash ${penceText(v)}` : 'Enter cash given';
            action.disabled = !v;
          },
        });
        action.addEventListener('click', () => pad.value() && take('cash', { tenderedPence: pad.value() }));
        const notes = [500, 1000, 2000, 5000].filter(n => n >= balance());
        const quick = h('div', { class: 'quick' },
          h('button', { class: 'btn quick-btn', onclick: () => take('cash', { tenderedPence: balance() }), text: `Exact ${penceText(balance())}` }),
          notes.map(n => h('button', { class: 'btn quick-btn', onclick: () => take('cash', { tenderedPence: n }), text: penceText(n) })));
        mount(panel, tabs, quick, pad.el, action);
      } else {
        const action = h('button', { class: 'btn btn-primary btn-big' });
        const help = h('p', { class: 'dialog-message' });
        const pad = keypad({
          mode: 'money', initial: balance(),
          onChange: v => {
            const amount = Math.min(v, balance());
            help.textContent = `Key ${penceText(amount)} into the card machine. When it is approved, press the button.`;
            action.textContent = `Card approved ${penceText(amount)}`;
            action.disabled = !v;
          },
        });
        action.addEventListener('click', () => {
          const amount = Math.min(pad.value(), balance());
          if (amount) take('card', { amountPence: amount, reference: reference.value.trim() || null });
        });
        mount(panel, tabs, help, pad.el,
          h('label', { class: 'note-field' }, h('span', { class: 'field-label', text: 'Card machine receipt no.' }), reference),
          action);
      }
    };

    render();
    return {
      title: `Payment — order ${order.number}`,
      wide: true,
      class: 'dialog-pay',
      body: h('div', { class: 'pay' }, summary, panel),
      actions: [h('button', { class: 'btn', onclick: () => close(false), text: 'Pay later' })],
    };
  });
}
