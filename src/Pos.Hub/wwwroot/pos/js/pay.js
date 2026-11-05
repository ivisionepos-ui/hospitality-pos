// Taking payment: cash (change worked out) or card on the Worldpay terminal, part payments, safe retries.
import { api, ApiError } from './api.js';
import { h, mount, money, newId, penceText, text, toast } from './dom.js';
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
 * The bill can be paid whole, in equal shares, or by items (each item paid once). Resolves true when fully paid.
 */
export async function payDialog(order) {
  if (!(await ensureShift())) return false;
  let ledger = await api.get(`/api/orders/${order.id}/payments`);
  if (ledger.balance.minorUnits <= 0) return true;

  return openDialog(close => {
    let method = 'cash';
    let split = 'whole';      // whole | equal | items
    let people = 2;           // equal shares: how many people
    let sharesPaid = 0;       //   and how many have paid here
    let shares = [];          //   what each of the rest pays (from the hub, to the penny)
    const chosen = new Set(); // items: lines chosen for this payment
    let itemsAmount = 0;      //   what they come to (from the hub: discounts and delivery shared fairly)
    // One id per payment attempt, kept until the hub confirms, so a retry never charges twice.
    let pendingId = null;
    let pendingKey = '';
    const summary = h('div', { class: 'pay-summary' });
    const splitPanel = h('div', { class: 'split-panel' });
    const panel = h('div', { class: 'pay-panel' });
    const reference = h('input', { class: 'text-input', type: 'text', maxlength: 40, placeholder: 'Optional', 'aria-label': 'Terminal receipt number' });
    const lines = order.lines.filter(l => !l.isVoided);

    const balance = () => ledger.balance.minorUnits;
    const target = () => (split === 'equal' ? (shares[0] ?? balance()) : split === 'items' ? itemsAmount : balance());
    const extra = () => (split === 'equal' ? { amountPence: target() } : split === 'items' ? { forLineIds: [...chosen] } : {});

    const showSummary = () => mount(summary,
      h('div', { class: 'pay-row' }, h('span', { text: 'Total' }), h('strong', { text: money(ledger.total) })),
      ledger.paid.minorUnits ? h('div', { class: 'pay-row' }, h('span', { text: 'Paid' }), h('span', { text: money(ledger.paid) })) : null,
      h('div', { class: 'pay-row pay-due' }, h('span', { text: 'To pay' }), h('strong', { text: money(ledger.balance) })),
      ledger.payments.length ? h('ul', { class: 'pay-list' }, ledger.payments.map(p =>
        h('li', {}, h('span', { text: `${p.method}${p.reference ? ` #${p.reference}` : ''}${p.forLineIds?.length ? ' (items)' : ''}` }), h('span', { text: money(p.amount) })))) : null,
      h('div', { class: 'seg seg-small split-modes', role: 'radiogroup', 'aria-label': 'Split the bill' },
        [['whole', 'Whole bill'], ['equal', 'Equal shares'], ['items', 'By items']].map(([id, label]) => h('button', {
          class: `seg-btn ${split === id ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(split === id),
          onclick: () => { split = id; refresh(); }, text: label,
        }))),
      splitPanel);

    // Equal shares are worked out from what is left, between the people who have not paid yet.
    const refresh = async () => {
      try {
        if (split === 'equal') {
          const left = Math.max(1, people - sharesPaid);
          shares = left >= 2 ? (await api.get(`/api/orders/${order.id}/split?parts=${left}`)).shares.map(m => m.minorUnits) : [balance()];
        }
        if (split === 'items') {
          for (const id of [...chosen]) if (ledger.paidLineIds.includes(id)) chosen.delete(id);
          itemsAmount = chosen.size ? (await api.post(`/api/orders/${order.id}/split-by-items`, { lineIds: [...chosen] })).shares[0].minorUnits : 0;
        }
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Could not work out the split.', 'error');
      }
      render();
    };

    const showSplit = () => {
      if (split === 'equal') {
        const left = Math.max(1, people - sharesPaid);
        mount(splitPanel,
          h('div', { class: 'people' },
            h('button', { class: 'btn btn-small', 'aria-label': 'Fewer people', disabled: people <= Math.max(2, sharesPaid + 1), onclick: () => { people--; refresh(); }, text: '−' }),
            h('strong', { class: 'people-count', text: `${people} people` }),
            h('button', { class: 'btn btn-small', 'aria-label': 'More people', disabled: people >= 20, onclick: () => { people++; refresh(); }, text: '+' })),
          h('p', { class: 'split-now', text: `Person ${sharesPaid + 1} of ${people} pays ${penceText(target())}` }),
          left > 1 ? h('p', { class: 'field-hint', text: `Then: ${shares.slice(1).map(penceText).join(', ')}` }) : null);
      } else if (split === 'items') {
        mount(splitPanel,
          h('div', { class: 'split-items' }, lines.map(l => {
            const paid = ledger.paidLineIds.includes(l.id);
            const on = chosen.has(l.id);
            return h('button', {
              class: `split-item ${on ? 'split-item-on' : ''}`, disabled: paid, 'aria-pressed': String(on),
              'aria-label': `${l.quantity} ${text(l.name)}`,
              onclick: () => { if (on) chosen.delete(l.id); else chosen.add(l.id); refresh(); },
            },
            h('span', { class: 'delivery-tick', text: on || paid ? '✓' : '' }),
            h('span', { class: 'split-item-name', text: `${l.quantity} × ${[l.size ? text(l.size) : null, text(l.name)].filter(Boolean).join(' ')}` }),
            h('span', { text: paid ? 'Paid' : money(l.lineTotal) }));
          })),
          h('p', { class: 'split-now', text: chosen.size ? `These items: ${penceText(itemsAmount)}` : 'Tap what this person had.' }),
          h('p', { class: 'field-hint', text: 'Discounts and the delivery charge are shared fairly between the items.' }));
      } else {
        mount(splitPanel);
      }
    };

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
        if (split === 'equal') sharesPaid += 1;
        chosen.clear();
        await refresh();
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Payment failed.', 'error', 6000);
      }
    };

    const render = () => {
      showSummary();
      showSplit();
      const amount = target();
      const tabs = h('div', { class: 'seg' },
        ['cash', 'card'].map(m => h('button', {
          class: `seg-btn ${method === m ? 'seg-on' : ''}`, 'aria-pressed': String(method === m),
          onclick: () => { method = m; render(); }, text: m === 'cash' ? 'Cash' : 'Card',
        })));

      if (!amount) {
        mount(panel, tabs, h('p', { class: 'empty-note', text: 'Choose the items first.' }));
      } else if (method === 'cash') {
        const action = h('button', { class: 'btn btn-primary btn-big' });
        const pad = keypad({
          mode: 'money',
          onChange: v => {
            action.textContent = v ? `Take cash ${penceText(v)}` : 'Enter cash given';
            action.disabled = !v || v < amount && split !== 'whole';
          },
        });
        action.addEventListener('click', () => pad.value() && take('cash', { tenderedPence: pad.value(), ...extra() }));
        const notes = [500, 1000, 2000, 5000].filter(n => n >= amount);
        const quick = h('div', { class: 'quick' },
          h('button', { class: 'btn quick-btn', onclick: () => take('cash', { tenderedPence: amount, ...extra() }), text: `Exact ${penceText(amount)}` }),
          notes.map(n => h('button', { class: 'btn quick-btn', onclick: () => take('cash', { tenderedPence: n, ...extra() }), text: penceText(n) })));
        mount(panel, tabs, quick, pad.el, action);
      } else {
        const action = h('button', { class: 'btn btn-primary btn-big' });
        const help = h('p', { class: 'dialog-message' });
        // Items are paid at exactly their amount; a whole bill or a share can be part paid by card.
        const fixed = split === 'items';
        const pad = fixed ? null : keypad({
          mode: 'money', initial: amount,
          onChange: v => {
            const pay = Math.min(v, amount);
            help.textContent = `Key ${penceText(pay)} into the card machine. When it is approved, press the button.`;
            action.textContent = `Card approved ${penceText(pay)}`;
            action.disabled = !v;
          },
        });
        if (fixed) {
          help.textContent = `Key ${penceText(amount)} into the card machine. When it is approved, press the button.`;
          action.textContent = `Card approved ${penceText(amount)}`;
        }

        action.addEventListener('click', () => {
          const pay = fixed ? amount : Math.min(pad.value(), amount);
          if (!pay) return;
          const body = { amountPence: pay, reference: reference.value.trim() || null };
          if (fixed) body.forLineIds = [...chosen];
          take('card', body);
        });
        mount(panel, tabs, help, pad?.el,
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
