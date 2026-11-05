// More → Printers (owner): how the receipt and kitchen printers are connected, test page, test drawer.
// More → Open drawer (no sale): a manager approves; every opening is recorded.
import { api, ApiError } from './api.js';
import { h, mount, toast } from './dom.js';
import { busy, openDialog, textDialog } from './dialogs.js';

const CONNECTIONS = [
  ['EscPosNetwork', 'Network printer', 'A thermal printer on the shop network (cable or Wi-Fi). Cuts and opens the drawer.'],
  ['EscPosWindows', 'USB printer', 'A thermal printer plugged into this hub PC and installed in Windows. Cuts and opens the drawer.'],
  ['Windows', 'Windows printer', 'Any printer installed in Windows, printed as a picture (no cutting or drawer).'],
  ['Pdf', 'Save as PDF', 'No printer: each printout is saved as a PDF file on the hub PC (for trying the system).'],
  ['None', 'Off', 'Nothing is printed.'],
];
const isThermal = output => output === 'EscPosNetwork' || output === 'EscPosWindows';
const problemText = e => (e instanceof ApiError ? e.message : 'Could not reach the hub.');

function printerCard(title, choice, installed, isReceipt, setup) {
  const card = h('section', { class: 'printer-card', 'aria-label': title });
  const render = () => {
    const info = CONNECTIONS.find(c => c[0] === choice.output);
    const needsName = choice.output === 'Windows' || choice.output === 'EscPosWindows';
    const name = h('input', { class: 'text-input', type: 'text', maxlength: 200, value: choice.printerName ?? '', list: `${title}-installed`, 'aria-label': `${title}: Windows printer name`, placeholder: 'Name as Windows shows it' });
    name.addEventListener('input', () => { choice.printerName = name.value; });
    const address = h('input', { class: 'text-input', type: 'text', maxlength: 60, value: choice.address ?? '', 'aria-label': `${title}: address`, placeholder: 'e.g. 192.168.1.50', inputmode: 'decimal' });
    address.addEventListener('input', () => { choice.address = address.value; });
    const cut = h('input', { type: 'checkbox', checked: choice.cut, 'aria-label': `${title}: cut the paper` });
    cut.addEventListener('change', () => { choice.cut = cut.checked; });
    const onCash = h('input', { type: 'checkbox', checked: setup.openDrawerOnCash, 'aria-label': 'Open the drawer for cash' });
    onCash.addEventListener('change', () => { setup.openDrawerOnCash = onCash.checked; });
    const seg = (label, options, value, set) => h('div', { class: 'field' }, h('span', { class: 'field-label', text: label }),
      h('div', { class: 'seg seg-small seg-wrap', role: 'radiogroup', 'aria-label': `${title}: ${label}` }, options.map(([v, text]) => h('button', {
        type: 'button', class: `seg-btn ${value === v ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(value === v),
        onclick: () => { set(v); render(); }, text,
      }))));
    mount(card,
      h('h3', { class: 'menu-heading', text: title }),
      seg('Connected by', CONNECTIONS.map(([v, text]) => [v, text]), choice.output, v => { choice.output = v; }),
      h('p', { class: 'field-hint', text: info?.[2] ?? '' }),
      choice.output === 'EscPosNetwork' ? h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Printer address (printed on its self-test page)' }), address) : null,
      needsName ? h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Windows printer name' }), name,
        h('datalist', { id: `${title}-installed` }, installed.map(p => h('option', { value: p })))) : null,
      needsName && !installed.length ? h('p', { class: 'field-hint', text: 'The list of installed printers shows when the hub runs on Windows.' }) : null,
      choice.output !== 'None' && choice.output !== 'Pdf' ? seg('Paper width', [[80, '80 mm'], [58, '58 mm']], choice.paperWidthMm, v => { choice.paperWidthMm = v; }) : null,
      isThermal(choice.output) ? h('label', { class: 'check' }, cut, h('span', { text: 'Cut the paper after each printout' })) : null,
      isReceipt && isThermal(choice.output) ? h('div', { class: 'drawer-box' },
        h('strong', { text: 'Cash drawer (plugs into this printer)' }),
        h('label', { class: 'check' }, onCash, h('span', { text: 'Open it for cash payments, cash refunds and paid in/out' })),
        seg('Drawer socket', [[2, 'Pin 2 (usual)'], [5, 'Pin 5']], choice.drawerPin, v => { choice.drawerPin = v; })) : null);
  };
  render();
  return card;
}

/** More → Printers (owner). */
export async function printersDialog() {
  let data;
  try {
    data = await busy(() => api.get('/api/settings/printers'));
  } catch (e) {
    toast(problemText(e), 'error');
    return;
  }

  const setup = structuredClone(data.setup);
  await openDialog(close => {
    const save = async () => {
      const saved = await busy(() => api.put('/api/settings/printers', setup));
      Object.assign(setup, structuredClone(saved));
      return saved;
    };
    const run = action => async () => {
      try {
        await action();
      } catch (e) {
        toast(problemText(e), 'error', 8000);
      }
    };
    const test = target => run(async () => {
      await save();
      const result = await busy(() => api.post('/api/settings/printers/test', { target }));
      toast(`Test page sent to ${result.output}.`, 'ok', 6000);
    });
    return {
      title: 'Printers',
      wide: true,
      body: h('div', { class: 'printers' },
        printerCard('Receipt printer', setup.receipt, data.installed, true, setup),
        printerCard('Kitchen printer', setup.kitchen, data.installed, false, setup)),
      actions: [
        h('button', { class: 'btn', onclick: test('Receipt'), text: 'Test receipt printer' }),
        h('button', { class: 'btn', onclick: test('Kitchen'), text: 'Test kitchen printer' }),
        h('button', { class: 'btn', onclick: run(async () => { await save(); await busy(() => api.post('/api/settings/printers/test-drawer')); toast('Drawer opened.', 'ok'); }), text: 'Test drawer' }),
        h('button', { class: 'btn btn-primary', onclick: run(async () => { await save(); toast('Printers saved. New printouts use them.', 'ok'); close(true); }), text: 'Save' }),
      ],
    };
  });
}

/** More → Open drawer (no sale). */
export async function noSale(withApproval) {
  const reason = await textDialog('Open the drawer (no sale)', { label: 'Reason', suggestions: ['Change for a note', 'Check the float', 'Put a cheque away'], ok: 'Open drawer' });
  if (!reason) return;
  try {
    const done = await withApproval(pin => busy(() => api.post('/api/drawer/open', { reason }, { managerPin: pin })).then(() => true));
    if (done) toast('Drawer opened. It is recorded as a no sale.', 'ok');
  } catch (e) {
    toast(problemText(e), 'error', 8000);
  }
}
