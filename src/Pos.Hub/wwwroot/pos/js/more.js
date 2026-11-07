// The "More" menu: menu, till shift, staff, X and Z reports, adding a till, failed prints, backups.
import { api } from './api.js';
import { h, mount, money, penceText, timeText, toast } from './dom.js';
import { alertDialog, amountDialog, busy, confirmDialog, openDialog, textDialog } from './dialogs.js';
import { ensureShift } from './pay.js';
import { withApproval } from './till.js';
import { soldOutDialog } from './editmenu.js';
import { addressFinder } from './address-finder.js';
import { changeMyPin, devicesDialog, staffDialog } from './staff.js';
import { discountLimitsDialog } from './discounts.js';
import { salesReportsDialog } from './reports.js';
import { menuSpreadsheetDialog } from './menusheet.js';
import { noSale, printersDialog } from './printers.js';

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
  const [summary, opens] = await busy(() => Promise.all([api.get('/api/shifts/current/x-report'), api.get('/api/drawer/opens')]));
  await openDialog(close => ({
    title: 'X report (this till, so far)',
    body: [
      shiftTable(summary),
      h('p', { class: 'field-hint', text: opens.length
        ? `Drawer opened with no sale today (all tills): ${opens.map(o => `${timeText(o.at)} ${o.staff} – ${o.reason} (approved by ${o.approvedBy})`).join('; ')}`
        : 'Drawer opened with no sale today: none.' }),
    ],
    actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })],
  }));
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
  const [code, health] = await busy(() => Promise.all([api.post('/api/devices/pairing-code'), api.get('/api/health')]));
  const addresses = health.networkAddresses ?? [];
  await openDialog(close => ({
    title: 'Add a till or kitchen screen',
    wide: true,
    body: h('div', { class: 'add-device' },
      h('ol', { class: 'steps' },
        h('li', {},
          h('strong', { text: 'Windows PC: ' }),
          'copy the hub folder to it and double-click "Connect this PC to the hub". When asked, type this address:',
          addresses.length ? h('ul', { class: 'addresses' }, addresses.map(a => h('li', { class: 'address', text: a }))) : h('p', { class: 'check-result check-bad', text: 'This PC has no network address: check its network cable or Wi-Fi.' })),
        h('li', {},
          h('strong', { text: 'Tablet or Android box: ' }),
          `install the shop certificate once (see the README), then open the address above in the browser.`),
        h('li', {}, 'On the new device choose Till or Kitchen screen and enter this code:',
          h('p', { class: 'pair-code', text: code.code }),
          h('p', { class: 'field-hint', text: `The code works until ${timeText(code.expiresAt)}. Certificate check code: ${health.caCheckCode}` }))),
      h('p', { class: 'field-hint', text: 'If the new device cannot connect: on THIS PC (the hub), double-click "Allow tills to connect" once, and make sure both are on the same network.' })),
    actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })],
  }));
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

/** Owner: shop name and address for receipts, and the postcode delivery distances are measured from. */
/** More → Eat-in and tables: no tables (eat-in goes straight to the menu) or tables (the till asks which table). */
async function eatInSettings() {
  let settings;
  try {
    settings = await busy(() => api.get('/api/settings/tables'));
  } catch (e) {
    toast(e.message ?? 'Could not reach the hub.', 'error');
    return;
  }

  const numbersOnly = settings.tables.every((t, i) => t === String(i + 1));
  let useTables = settings.useTables;
  let named = !numbersOnly && settings.tables.length > 0;
  await openDialog(close => {
    const body = h('div', { class: 'eat-in-settings' });
    const count = h('input', { class: 'text-input count-input', type: 'number', inputmode: 'numeric', min: 1, max: 200, value: String(numbersOnly && settings.tables.length ? settings.tables.length : 10), 'aria-label': 'Number of tables' });
    const names = h('textarea', { class: 'text-input', rows: 6, 'aria-label': 'Table names, one per line', placeholder: 'One per line, e.g.\n1\n2\nWindow 1\nGarden 1' });
    names.value = settings.tables.join('\n');
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    const option = (value, title, hint) => h('button', {
      class: `choice ${useTables === value ? 'choice-on' : ''}`, role: 'radio', 'aria-checked': String(useTables === value),
      onclick: () => { useTables = value; render(); },
    }, h('strong', { text: title }), h('span', { text: hint }));

    const render = () => mount(body,
      h('div', { class: 'choices', role: 'radiogroup', 'aria-label': 'Eat-in orders' },
        option(false, 'No tables', 'Eat in goes straight to the menu.'),
        option(true, 'Use tables', 'Eat in asks which table first. The table is printed on kitchen tickets and receipts.')),
      useTables ? h('div', { class: 'field' },
        h('div', { class: 'seg seg-small', role: 'radiogroup', 'aria-label': 'How the tables are named' },
          h('button', { class: `seg-btn ${named ? '' : 'seg-on'}`, role: 'radio', 'aria-checked': String(!named), onclick: () => { named = false; render(); }, text: 'Numbered' }),
          h('button', { class: `seg-btn ${named ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(named), onclick: () => { named = true; render(); }, text: 'My own names' })),
        named
          ? h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Table names, one per line (up to 200)' }), names)
          : h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Number of tables (numbered 1 to this)' }), count)) : null,
      problem);

    const save = async () => {
      problem.textContent = '';
      const request = { useTables };
      if (useTables && named) request.tables = names.value.split('\n').map(t => t.trim()).filter(Boolean);
      else if (useTables) request.count = Number(count.value);
      try {
        const saved = await busy(() => api.put('/api/settings/tables', request));
        toast(saved.useTables ? `Eat-in orders now ask for the table (${saved.tables.length} tables).` : 'Eat-in orders now go straight to the menu.', 'ok');
        close();
      } catch (e) {
        problem.textContent = e.message;
      }
    };

    render();
    return {
      title: 'Eat-in orders',
      class: 'dialog-narrow',
      body,
      actions: [h('button', { class: 'btn', onclick: () => close(), text: 'Cancel' }), h('button', { class: 'btn btn-primary', onclick: save, text: 'Save' })],
    };
  });
}

async function shopDetails() {
  const d = await busy(() => api.get('/api/settings/shop'));
  await openDialog(close => {
    const field = (label, value, attrs = {}) => {
      const el = h('input', { class: 'text-input', type: 'text', value: value ?? '', autocomplete: 'off', ...attrs });
      return { el, field: h('label', { class: 'field' }, h('span', { class: 'field-label', text: label }), el) };
    };
    const name = field('Shop name (top of the receipt)', d.businessName, { maxlength: 60 });
    const line1 = field('Address line 1', d.addressLine1, { maxlength: 80, placeholder: 'e.g. 12 High Street' });
    const line2 = field('Address line 2 (optional)', d.addressLine2, { maxlength: 80 });
    const town = field('Town', d.town, { maxlength: 60 });
    const postcode = field('Postcode (delivery distances are measured from here)', d.postcode, { maxlength: 9, class: 'text-input postcode-input' });
    const phone = field('Phone number', d.phone, { type: 'tel', maxlength: 20 });
    const vat = field('VAT number (if VAT registered)', d.vatNumber, { maxlength: 20, placeholder: 'e.g. GB 123 4567 89' });
    const message = field('Message at the bottom of receipts', d.receiptMessage, { maxlength: 120, placeholder: 'e.g. Thank you for your order' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    const located = h('p', { class: 'check-result', role: 'status' });

    // Is the postcode real? Delivery distances are measured from it, so check it before saving.
    const checkPostcode = async () => {
      if (!postcode.el.value.trim()) { located.textContent = ''; return; }
      located.className = 'check-result';
      located.textContent = 'Checking…';
      try {
        const place = await api.get(`/api/delivery/locate?postcode=${encodeURIComponent(postcode.el.value)}`);
        postcode.el.value = place.postcode;
        if (!town.el.value.trim() && place.town) town.el.value = place.town;
        located.className = 'check-result check-ok';
        located.textContent = `✓ ${place.postcode} found${place.town ? ` (${place.town})` : ''}. Delivery distances will be measured from here.`;
      } catch (e) {
        located.className = 'check-result check-bad';
        located.textContent = e.code === 'LookupUnavailable'
          ? 'The postcode cannot be checked without internet. It will be checked the first time a delivery is priced.'
          : e.message;
      }
    };
    postcode.el.addEventListener('keydown', e => { if (e.key === 'Enter') checkPostcode(); });

    // Find the shop's address with the chosen address service; the shop's own name is not repeated in the address.
    const finder = addressFinder(address => {
      const shopName = name.el.value.trim().toLowerCase();
      const rest = (address.line2 ?? '').split(',').map(p => p.trim()).filter(p => p && p.toLowerCase() !== shopName);
      line1.el.value = address.line1;
      line2.el.value = rest.join(', ');
      town.el.value = address.town ?? '';
      postcode.el.value = address.postcode;
      return checkPostcode();
    });
    return {
      title: 'Shop details',
      wide: true,
      focus: name.el,
      body: h('div', { class: 'customer-form' },
        h('div', { class: 'customer-col' }, name.field, finder.field, finder.list, line1.field, line2.field, problem),
        h('div', { class: 'customer-col' }, town.field,
          h('div', { class: 'postcode-row' }, postcode.field, h('button', { class: 'btn btn-kitchen', onclick: checkPostcode, text: 'Check postcode' })),
          located, phone.field, vat.field, message.field,
          h('p', { class: 'field-hint', text: 'These print on every receipt. UK VAT receipts must show the VAT number if the business is VAT registered.' }))),
      actions: [
        h('button', { class: 'btn', onclick: () => close(), text: 'Cancel' }),
        h('button', {
          class: 'btn btn-primary', text: 'Save',
          onclick: async () => {
            problem.textContent = '';
            try {
              const saved = await busy(() => api.put('/api/settings/shop', {
                businessName: name.el.value, addressLine1: line1.el.value, addressLine2: line2.el.value, town: town.el.value,
                postcode: postcode.el.value, phone: phone.el.value, vatNumber: vat.el.value, receiptMessage: message.el.value,
              }));
              toast(`Shop details saved for ${saved.businessName}. New receipts use them.`, 'ok');
              close();
            } catch (e) {
              problem.textContent = e.message;
            }
          },
        }),
      ],
    };
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
            can('EditMenu') ? item('Menu spreadsheet (Excel)', menuSpreadsheetDialog) : null,
            item('Sold out items', () => soldOutDialog(till)),
            can('ManageDevices') ? item('Discount limits', discountLimitsDialog) : null),
          section('Till',
            item('Open till', async () => { if (await ensureShift()) toast('The till is open.', 'ok'); }),
            item('Close till (cash count)', closeShift),
            item('Paid in', () => cashMovement('paid-in')),
            item('Paid out', () => cashMovement('paid-out')),
            item('Open drawer (no sale)', () => noSale(withApproval))),
          section('Staff and devices',
            can('ManageStaff') ? item('Staff', () => staffDialog(till.ctx.me.staff), true) : null,
            item('Change my PIN', () => changeMyPin(till.ctx.me.staff, till.ctx.logout)),
            can('ManageDevices') ? item('Tills and screens', devicesDialog) : null,
            can('ManageDevices') ? item('Printers', printersDialog) : null,
            can('ManageDevices') ? item('Add a till or screen', addTill) : null),
          section('Reports and shop',
            can('ManageDevices') ? item('Shop details', shopDetails) : null,
            can('ManageDevices') ? item('Eat-in and tables', eatInSettings) : null,
            can('ViewReports') ? item('Sales reports', salesReportsDialog, true) : null,
            can('ViewReports') ? item('X report (this till)', xReport) : null,
            can('ViewReports') ? item('End of day (Z report)', zReport) : null,
            item('Failed printouts', failedPrints),
            can('ViewReports') ? item('Back up now', backupNow) : null,
            can('ManageDevices') ? item('Address lookup', addressLookup) : null,
            can('ManageDevices') ? item('Address files', addressFiles) : null)),
        h('p', { class: 'version', text: `Version ${window.posVersion ?? ''} · ${till.ctx.me.staff.name} (${till.ctx.me.staff.role})` }),
      ],
    };
  });
}
