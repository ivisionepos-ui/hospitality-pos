// The "More" menu: till shift, X and Z reports, adding a till, failed prints, backups.
import { api } from './api.js';
import { h, mount, money, penceText, timeText, toast } from './dom.js';
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

/** The shop's Royal Mail address files: what is loaded, add a file, reload the folder (owner). */
async function addressFiles() {
  let files = await busy(() => api.get('/api/addresses/files'));
  await openDialog(close => {
    const body = h('div', { class: 'menu-list' });
    const picker = h('input', { type: 'file', accept: '.csv,text/csv', multiple: true, class: 'file-input', 'aria-label': 'Choose address files' });
    const render = () => mount(body,
      h('p', { class: 'dialog-message', text: 'Royal Mail address files (one .csv per postcode area, e.g. B12.csv). Staff then pick the house from the postcode. Files in the hub’s "addresses" folder load by themselves when the hub starts.' }),
      files.length
        ? h('table', { class: 'report' }, h('tbody', {}, files.map(f => h('tr', {},
          h('th', { scope: 'row', text: f.name }),
          h('td', { text: `${f.addresses.toLocaleString('en-GB')} addresses · ${f.postcodes} postcodes · ${new Date(f.importedAt).toLocaleDateString('en-GB')}` })))))
        : h('p', { class: 'empty-note', text: 'No address files yet.' }),
      h('label', { class: 'btn btn-primary file-btn' }, 'Add address files…', picker),
      h('button', {
        class: 'btn', text: 'Reload the addresses folder',
        onclick: async () => {
          const loaded = await busy(() => api.post('/api/addresses/files/reload'));
          files = await api.get('/api/addresses/files');
          toast(loaded.length ? `Loaded ${loaded.map(f => f.name).join(', ')}.` : 'No new or changed files in the folder.', 'ok');
          render();
        },
      }));
    picker.addEventListener('change', async () => {
      for (const file of picker.files) {
        try {
          const result = await busy(() => api.upload(`/api/addresses/files?name=${encodeURIComponent(file.name)}`, file));
          toast(`${result.name}: ${result.addresses.toLocaleString('en-GB')} addresses loaded.`, 'ok');
        } catch (e) {
          toast(`${file.name}: ${e.message}`, 'error', 8000);
        }
      }

      files = await api.get('/api/addresses/files');
      picker.value = '';
      render();
    });
    render();
    return { title: 'Address files', wide: true, body, actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })] };
  });
}

const PROVIDERS = [
  { id: 'Google', title: 'Google', text: 'Find any address by typing part of it. Needs internet and a Google Maps Platform key (Places API (New)); free up to Google’s monthly allowance, then paid per search.', key: 'googleApiKey', keySet: 'googleKeySet', ending: 'googleKeyEnding', keyLabel: 'Google API key' },
  { id: 'RoyalMail', title: 'Royal Mail online', text: 'Official Royal Mail addresses: type a postcode and pick the house. Needs internet and a Royal Mail AddressNow (Loqate) key; paid per address chosen.', key: 'royalMailApiKey', keySet: 'royalMailKeySet', ending: 'royalMailKeyEnding', keyLabel: 'Royal Mail AddressNow key' },
  { id: 'LocalFile', title: 'Local Royal Mail file', text: 'The Royal Mail address files bought for your area, kept on the hub (More → Address files). No internet, no cost per search.' },
];

/** Owner: choose where "Find address" looks, with keys for the online services and a test. */
async function addressLookup() {
  let view = await busy(() => api.get('/api/settings/address-lookup'));
  await openDialog(close => {
    let chosen = view.provider;
    let fallback = view.fallbackToLocalFile;
    const keys = {};
    const body = h('div', { class: 'lookup-settings' });
    const testText = h('input', { class: 'text-input', type: 'search', placeholder: 'e.g. a postcode near the shop', 'aria-label': 'Test search', autocomplete: 'off' });
    const testResult = h('div', { class: 'test-result', 'aria-live': 'polite' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });

    const render = () => mount(body,
      h('div', { class: 'provider-cards', role: 'radiogroup', 'aria-label': 'Address lookup' }, PROVIDERS.map((p, i) => {
        const on = chosen === p.id;
        const keyInput = p.key ? h('input', {
          class: 'text-input', type: 'password', autocomplete: 'off', 'aria-label': p.keyLabel,
          placeholder: view[p.keySet] ? `Saved: …${view[p.ending]} (type a new one to change)` : `Paste the key here`,
        }) : null;
        if (keyInput) {
          keyInput.value = keys[p.key] ?? '';
          keyInput.addEventListener('input', () => { keys[p.key] = keyInput.value; });
        }
        return h('div', { class: `provider ${on ? 'provider-on' : ''}` },
          h('button', { class: 'provider-pick', role: 'radio', 'aria-checked': String(on), onclick: () => { chosen = p.id; render(); } },
            h('span', { class: 'provider-number', text: String(i + 1) }),
            h('span', { class: 'provider-title', text: p.title }),
            p.id === 'LocalFile' ? h('span', { class: 'provider-note', text: `${view.localAddresses.toLocaleString('en-GB')} addresses loaded` })
              : view[p.keySet] ? h('span', { class: 'provider-note', text: 'Key saved' }) : h('span', { class: 'provider-note provider-missing', text: 'No key yet' })),
          h('p', { class: 'field-hint', text: p.text }),
          keyInput,
          h('button', {
            class: 'btn btn-small', text: `Test ${p.title}`,
            onclick: async () => {
              if (p.key && keys[p.key]) { problem.textContent = 'Save the new key first, then test it.'; return; }
              mount(testResult, h('p', { class: 'field-hint', text: 'Testing…' }));
              try {
                const r = await api.post(`/api/settings/address-lookup/test?provider=${p.id}&q=${encodeURIComponent(testText.value || 'B12 0YR')}`);
                mount(testResult, h('p', { class: 'check-result check-ok', text: `${p.title} works: ${r.suggestions.length} found${r.suggestions[0] ? `, e.g. ${r.suggestions[0].label}` : ''}.` }));
              } catch (e) {
                mount(testResult, h('p', { class: 'check-result check-bad', text: `${p.title}: ${e.message}` }));
              }
            },
          }));
      })),
      h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Test search (postcode or address)' }), testText),
      testResult,
      h('button', {
        class: `switch ${fallback ? 'switch-on-good' : ''}`, role: 'switch', 'aria-checked': String(fallback),
        onclick: () => { fallback = !fallback; render(); },
      }, h('span', { class: 'switch-knob' }), 'If the online service fails, use the local file'),
      problem);

    render();
    return {
      title: 'Address lookup',
      wide: true,
      body,
      actions: [
        h('button', { class: 'btn', onclick: () => close(), text: 'Cancel' }),
        h('button', {
          class: 'btn btn-primary', text: 'Save',
          onclick: async () => {
            problem.textContent = '';
            try {
              view = await busy(() => api.put('/api/settings/address-lookup', {
                provider: chosen, googleApiKey: keys.googleApiKey || null, royalMailApiKey: keys.royalMailApiKey || null, fallbackToLocalFile: fallback,
              }));
              for (const k of Object.keys(keys)) delete keys[k];
              toast(`Find address now uses: ${PROVIDERS.find(p => p.id === view.provider).title}.`, 'ok');
              render();
            } catch (e) {
              problem.textContent = e.message;
            }
          },
        }),
      ],
    };
  });
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
  // Columns, so every option is visible at once on a 768-pixel-high till screen (no hidden scrolling).
  const section = (title, ...buttons) => {
    const shown = buttons.filter(Boolean);
    return shown.length ? h('section', { class: 'more-col' }, h('h3', { class: 'menu-heading', text: title }), shown) : null;
  };
  const item = (label, action, primary = false) => h('button', { class: `btn menu-btn ${primary ? 'menu-btn-main' : ''}`, onclick: run(close, action), text: label });
  let close;
  return openDialog(c => {
    close = c;
    return {
      title: 'More',
      wide: true,
      body: [
        h('div', { class: 'more-grid' },
          section('Menu',
            can('EditMenu') ? item('Edit menu', () => till.enterEditMode(), true) : null,
            item('Sold out items', () => soldOutDialog(till))),
          section('Till',
            item('Open till', async () => { if (await ensureShift()) toast('The till is open.', 'ok'); }),
            item('Close till (cash count)', closeShift),
            item('Paid in', () => cashMovement('paid-in')),
            item('Paid out', () => cashMovement('paid-out'))),
          section('Reports and shop',
            can('ViewReports') ? item('X report (this till)', xReport) : null,
            can('ViewReports') ? item('End of day (Z report)', zReport) : null,
            item('Failed printouts', failedPrints),
            can('ViewReports') ? item('Back up now', backupNow) : null,
            can('ManageDevices') ? item('Add a till', addTill) : null,
            can('ManageDevices') ? item('Address lookup', addressLookup) : null,
            can('ManageDevices') ? item('Address files', addressFiles) : null)),
        h('p', { class: 'version', text: `Version ${window.posVersion ?? ''} · ${till.ctx.me.staff.name} (${till.ctx.me.staff.role})` }),
      ],
    };
  });
}
