// Back-office sales reports (managers and owners): any range of trading days, each table savable as a spreadsheet.
import { api, ApiError } from './api.js';
import { h, mount, money, toast } from './dom.js';
import { busy, openDialog } from './dialogs.js';

const TABS = [
  ['summary', 'Summary'], ['items', 'Items'], ['categories', 'Categories'], ['hours', 'Hours'],
  ['days', 'Days'], ['staff', 'Staff'], ['payments', 'Payments'], ['vat', 'VAT'],
];
const METHOD = { Cash: 'Cash', Card: 'Card', DeliveryApp: 'Delivery app', Online: 'Online' };

// Dates as "YYYY-MM-DD" trading days; worked out in UTC so a clock change never shifts a day.
const toDate = s => new Date(`${s}T00:00:00Z`);
const iso = d => d.toISOString().slice(0, 10);
const addDays = (s, n) => { const d = toDate(s); d.setUTCDate(d.getUTCDate() + n); return iso(d); };
const nice = s => toDate(s).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });

/** The preset ranges, from the hub's own "today" (the trading day, which starts in the early morning). */
function presets(today) {
  const d = toDate(today);
  const monday = addDays(today, -((d.getUTCDay() + 6) % 7));
  const firstOfMonth = `${today.slice(0, 8)}01`;
  const lastMonthEnd = addDays(firstOfMonth, -1);
  return [
    ['Today', today, today],
    ['Yesterday', addDays(today, -1), addDays(today, -1)],
    ['This week', monday, today],
    ['Last 7 days', addDays(today, -6), today],
    ['This month', firstOfMonth, today],
    ['Last month', `${lastMonthEnd.slice(0, 8)}01`, lastMonthEnd],
  ];
}

function table(headings, rows, numericFrom = 1) {
  return h('table', { class: 'report report-grid' },
    h('thead', {}, h('tr', {}, headings.map((t, i) => h('th', { scope: 'col', class: i >= numericFrom ? 'num' : '', text: t })))),
    h('tbody', {}, rows.map(r => h('tr', {}, r.map((c, i) => h('td', { class: i >= numericFrom ? 'num' : '', text: c }))))));
}

const empty = () => h('p', { class: 'empty-note', text: 'No sales in these days.' });

/** Saves one table as a CSV file (opens in Excel). */
async function download(from, to, tab) {
  try {
    const blob = await busy(() => api.blob(`/api/reports/sales.csv?from=${from}&to=${to}&table=${tab}`));
    const url = URL.createObjectURL(blob);
    const link = h('a', { href: url, download: `sales-${tab}-${from}-to-${to}.csv` });
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    toast('Spreadsheet saved to Downloads.', 'ok');
  } catch (e) {
    toast(e instanceof ApiError ? e.message : 'Could not save the spreadsheet.', 'error');
  }
}

function view(report, tab) {
  const s = report.summary;
  switch (tab) {
    case 'summary':
      return [
        h('div', { class: 'kpis' }, [
          ['Sales', money(s.sales)], ['Orders', String(s.orders)], ['Average order', money(s.averageOrder)], ['Net sales (after refunds)', money(s.netSales)],
        ].map(([label, value]) => h('div', { class: 'kpi' }, h('span', { class: 'kpi-label', text: label }), h('strong', { class: 'kpi-value', text: value })))),
        h('div', { class: 'report-cols' },
          table(['', 'Amount'], [
            ['Items sold', String(s.itemsSold)], ['Offer savings', money(s.offerSavings)], ['Discounts', money(s.discounts)],
            ['Delivery charges', money(s.deliveryFees)], ['Service charges', money(s.serviceCharges)], ['Refunds', money(s.refunds)],
            ['Cancelled orders', `${s.cancelledOrders} · ${money(s.cancelledValue)}`], ['Items removed', String(s.voidedItems)],
          ]),
          report.types.length ? table(['Type', 'Orders', 'Sales'], report.types.map(r => [r.name, String(r.quantity), money(r.sales)])) : null),
      ];
    case 'items':
    case 'categories':
      return report[tab].length
        ? table([tab === 'items' ? 'Item' : 'Category', 'Sold', 'Sales'], report[tab].map(r => [r.name, String(r.quantity), money(r.sales)]))
        : empty();
    case 'hours':
    case 'days': {
      const rows = report[tab];
      if (!rows.length) return empty();
      const top = Math.max(...rows.map(r => r.sales.minorUnits), 1);
      return h('div', { class: 'bars' }, rows.map(r => h('div', { class: 'bar-row' },
        h('span', { class: 'bar-label', text: tab === 'days' ? nice(r.name) : r.name }),
        h('span', { class: 'bar-track' }, h('span', { class: 'bar', style: { width: `${Math.max(1, Math.round(r.sales.minorUnits / top * 100))}%` } })),
        h('span', { class: 'bar-value', text: `${r.quantity} ${r.quantity === 1 ? 'order' : 'orders'} · ${money(r.sales)}` }))));
    }
    case 'staff':
      return report.staff.length
        ? table(['Staff', 'Orders', 'Sales', 'Discounts', 'Items removed', 'Refunds'], report.staff.map(r => [
          r.name, String(r.orders), money(r.sales), r.discounts ? `${r.discounts} · ${money(r.discountValue)}` : '0',
          String(r.itemsRemoved), r.refunds ? `${r.refunds} · ${money(r.refundedValue)}` : '0']))
        : empty();
    case 'payments':
      return report.payments.length
        ? table(['Method', 'Taken', 'Refunded', 'Net', 'Tips'], report.payments.map(r => [METHOD[r.method] ?? r.method, money(r.taken), money(r.refunded), money(r.net), money(r.tips)]))
        : empty();
    case 'vat':
      return report.vat.length
        ? [table(['VAT rate', 'Sales incl. VAT', 'Net', 'VAT'], report.vat.map(v => [`${v.code} ${v.percent}%`, money(v.gross), money(v.net), money(v.vat)])),
          h('p', { class: 'field-hint', text: 'Completed sales less refunds for what was sold. Each day’s Z report remains the record for that day.' })]
        : empty();
    default:
      return null;
  }
}

/** More → Sales reports. */
export async function salesReportsDialog() {
  let report;
  try {
    report = await busy(() => api.get('/api/reports/sales'));
  } catch (e) {
    toast(e instanceof ApiError ? e.message : 'Could not load the report.', 'error');
    return;
  }

  const today = report.from;
  let tab = 'summary';
  await openDialog(close => {
    const head = h('div', { class: 'report-head' });
    const tabs = h('div', { class: 'seg seg-small report-tabs', role: 'tablist' });
    const content = h('div', { class: 'report-body', role: 'tabpanel' });
    const fromInput = h('input', { class: 'text-input date-input', type: 'date', value: report.from, max: today, 'aria-label': 'From' });
    const toInput = h('input', { class: 'text-input date-input', type: 'date', value: report.to, max: today, 'aria-label': 'To' });

    const load = async (from, to) => {
      try {
        report = await busy(() => api.get(`/api/reports/sales?from=${from}&to=${to}`));
        render();
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Could not load the report.', 'error', 6000);
      }
    };

    const render = () => {
      fromInput.value = report.from;
      toInput.value = report.to;
      mount(head,
        h('div', { class: 'chips' }, presets(today).map(([label, from, to]) => h('button', {
          class: `chip ${report.from === from && report.to === to ? 'chip-on' : ''}`, onclick: () => load(from, to), text: label,
        }))),
        h('div', { class: 'range' },
          fromInput, h('span', { text: 'to' }), toInput,
          h('button', { class: 'btn btn-small', onclick: () => load(fromInput.value, toInput.value), text: 'Show' })),
        h('p', { class: 'report-range', text: report.from === report.to ? nice(report.from) : `${nice(report.from)} – ${nice(report.to)}` }));
      mount(tabs, TABS.map(([id, label]) => h('button', {
        class: `seg-btn ${tab === id ? 'seg-on' : ''}`, role: 'tab', 'aria-selected': String(tab === id), onclick: () => { tab = id; render(); }, text: label,
      })));
      mount(content, view(report, tab));
    };
    render();
    return {
      title: 'Sales reports',
      wide: true,
      class: 'dialog-reports',
      body: [head, tabs, content],
      actions: [
        h('button', { class: 'btn', onclick: () => download(report.from, report.to, tab), text: 'Save as spreadsheet' }),
        h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' }),
      ],
    };
  });
}
