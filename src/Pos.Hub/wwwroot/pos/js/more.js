// The "More" menu: till shift, X and Z reports, adding a till, failed prints, backups.
import { api } from './api.js';
import { h, money, penceText, timeText, toast } from './dom.js';
import { alertDialog, amountDialog, busy, confirmDialog, openDialog, textDialog } from './dialogs.js';
import { ensureShift } from './pay.js';
import { withApproval } from './till.js';
import { soldOutDialog } from './editmenu.js';

function table(rows) {
  return h('table', { class: 'report' }, h('tbody', {}, rows.filter(Boolean).map(([label, value, strong]) =>
    h('tr', { class: strong ? 'report-strong' : '' }, h('th', { scope: 'row', text: label }), h('td', { text: value })))));
}

function methodRows(methods) {
  return methods.map(m => [`${m.method} (${m.payments})`, money({ minorUnits: m.taken.minorUnits - m.refunded.minorUnits })]);
}

function shiftTable(s) {
  return table([
    ['Opened', timeText(s.openedAt)],
    ['Float', money(s.openingFloat)],
    ['Cash sales', money(s.cashSales)],
    s.cashRefunds.minorUnits ? ['Cash refunds', `-${money(s.cashRefunds)}`] : null,
    s.paidIn.minorUnits ? ['Paid in', money(s.paidIn)] : null,
    s.paidOut.minorUnits ? ['Paid out', `-${money(s.paidOut)}`] : null,
    ['Cash expected in drawer', money(s.expectedCash), true],
    s.countedCash ? ['Cash counted', money(s.countedCash), true] : null,
    s.variance ? [s.variance.minorUnits < 0 ? 'Short' : s.variance.minorUnits > 0 ? 'Over' : 'Exact', money(s.variance), true] : null,
    ...methodRows(s.byMethod),
  ]);
}

async function closeShift() {
  const shift = await api.get('/api/shifts/current');
  if (!shift) { toast('This till has no open shift.', 'info'); return; }
  const counted = await amountDialog('Close the till', {
    message: 'Count all the cash in the drawer and enter the total. (The expected amount is shown after, so the count is honest.)',
    ok: 'Close till', allowZero: true,
  });
  if (counted === null) return;
  const summary = await busy(() => api.post('/api/shifts/close', { countedCashPence: counted }));
  await openDialog(close => ({ title: 'Till closed', body: shiftTable(summary), actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })] }));
}

async function cashMovement(kind) {
  const amount = await amountDialog(kind === 'paid-in' ? 'Cash into the drawer' : 'Cash out of the drawer', { ok: 'Next' });
  if (!amount) return;
  const reason = await textDialog('Reason', { suggestions: kind === 'paid-in' ? ['Extra change', 'Float top-up'] : ['Milk', 'Driver fuel', 'Supplier'], ok: 'Save' });
  if (!reason) return;
  const done = await withApproval(pin => busy(() => api.post(`/api/shifts/${kind}`, { amountPence: amount, reason }, { managerPin: pin })));
  if (done) toast(`${penceText(amount)} ${kind === 'paid-in' ? 'paid in' : 'paid out'}.`, 'ok');
}

async function xReport() {
  const summary = await busy(() => api.get('/api/shifts/current/x-report'));
  await openDialog(close => ({ title: 'X report (this till, so far)', body: shiftTable(summary), actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })] }));
}

async function zReport() {
  if (!(await confirmDialog('End of day (Z report)', 'Every till must be closed first. After the Z report no more shifts can be opened today.', 'Take Z report'))) return;
  const z = await busy(() => api.post('/api/reports/z', { businessDate: null }));
  const vat = z.vat.reduce((s, v) => s + v.vat.minorUnits, 0);
  await openDialog(close => ({
    title: `Z report ${z.zNumber} — ${z.businessDate}`,
    body: table([
      ['Orders', String(z.sales.orders)],
      ['Sales', money(z.sales.total), true],
      ...methodRows(z.payments),
      ['VAT', penceText(vat)],
      ['Cash over / short', money(z.cashVariance)],
      z.exceptions.refunds ? ['Refunds', `${z.exceptions.refunds} · ${money(z.exceptions.refundedValue)}`] : null,
      z.exceptions.cancelledOrders ? ['Cancelled orders', `${z.exceptions.cancelledOrders} · ${money(z.exceptions.cancelledValue)}`] : null,
      z.exceptions.voidedItems ? ['Items removed', `${z.exceptions.voidedItems} · ${money(z.exceptions.voidedValue)}`] : null,
      z.exceptions.openOrders ? ['Orders still open', String(z.exceptions.openOrders)] : null,
    ]),
    actions: [
      h('button', { class: 'btn', onclick: async () => { await api.post('/api/reports/z/print', { businessDate: z.businessDate }); toast('Z report printing.', 'ok'); }, text: 'Print' }),
      h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' }),
    ],
  }));
}

async function addTill() {
  const code = await busy(() => api.post('/api/devices/pairing-code'));
  await alertDialog('Add a till', `On the new till, open this page and enter the code ${code.code}. It works for a few minutes (until ${timeText(code.expiresAt)}).`);
}

async function failedPrints() {
  const jobs = await busy(() => api.get('/api/print-jobs?status=Failed'));
  await openDialog(close => ({
    title: 'Failed printouts',
    body: jobs.length ? h('div', { class: 'menu-list' }, jobs.map(j => h('div', { class: 'order-row' },
      h('span', { class: 'order-meta', text: `${timeText(j.createdAt)} ${j.job.kind} — ${j.lastError ?? ''}` }),
      h('button', { class: 'btn btn-small', onclick: async () => { await api.post(`/api/print-jobs/${j.job.id}/retry`); toast('Trying again.', 'info'); close(); }, text: 'Retry' }))))
      : h('p', { class: 'empty-note', text: 'Nothing has failed to print.' }),
  }));
}

async function backupNow() {
  const result = await busy(() => api.post('/api/backups'));
  toast(result.copyError ? `Backup made, but the copy failed: ${result.copyError}` : `Backup made: ${result.file}`, result.copyError ? 'error' : 'ok', 6000);
}

/** Opens the More menu for the till screen. */
export function moreDialog(till) {
  const can = p => till.can(p);
  const run = (close, action) => async () => {
    close();
    try { await action(); } catch (e) { till.showError(e); }
  };
  return openDialog(close => ({
    title: 'More',
    class: 'dialog-narrow',
    body: h('div', { class: 'menu-list' },
      h('h3', { class: 'menu-heading', text: 'Till' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, async () => { if (await ensureShift()) toast('The till is open.', 'ok'); }), text: 'Open till' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, closeShift), text: 'Close till (cash count)' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, () => cashMovement('paid-in')), text: 'Paid in' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, () => cashMovement('paid-out')), text: 'Paid out' }),
      can('ViewReports') ? h('h3', { class: 'menu-heading', text: 'Reports' }) : null,
      can('ViewReports') ? h('button', { class: 'btn menu-btn', onclick: run(close, xReport), text: 'X report (this till)' }) : null,
      can('ViewReports') ? h('button', { class: 'btn menu-btn', onclick: run(close, zReport), text: 'End of day (Z report)' }) : null,
      h('h3', { class: 'menu-heading', text: 'Shop' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, failedPrints), text: 'Failed printouts' }),
      can('ViewReports') ? h('button', { class: 'btn menu-btn', onclick: run(close, backupNow), text: 'Back up now' }) : null,
      can('ManageDevices') ? h('button', { class: 'btn menu-btn', onclick: run(close, addTill), text: 'Add a till' }) : null,
      h('h3', { class: 'menu-heading', text: 'Menu' }),
      h('button', { class: 'btn menu-btn', onclick: run(close, () => soldOutDialog(till)), text: 'Sold out items' }),
      can('EditMenu') ? h('button', { class: 'btn menu-btn', onclick: run(close, () => till.enterEditMode()), text: 'Edit menu' }) : null),
  }));
}
