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
const TYPES = [
  ['EscPos', 'Epson, Xprinter and most (ESC/POS)'],
  ['StarGraphic', 'Star TSP100 / TSP143'],
];
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
      isThermal(choice.output) ? seg('Printer type', TYPES, choice.commands ?? 'EscPos', v => { choice.commands = v; if (v === 'StarGraphic') choice.drawerPin = 2; }) : null,
      isThermal(choice.output) && choice.commands === 'StarGraphic' ? h('p', { class: 'field-hint', text: 'Star TSP100 printers have no built-in fonts; the till sends each printout as a picture in Star graphic mode.' }) : null,
      choice.output !== 'None' && choice.output !== 'Pdf' ? seg('Paper width', [[80, '80 mm'], [58, '58 mm']], choice.paperWidthMm, v => { choice.paperWidthMm = v; }) : null,
      isThermal(choice.output) ? h('label', { class: 'check' }, cut, h('span', { text: 'Cut the paper after each printout' })) : null,
      isReceipt && isThermal(choice.output) ? h('div', { class: 'drawer-box' },
        h('strong', { text: 'Cash drawer (plugs into this printer)' }),
        h('label', { class: 'check' }, onCash, h('span', { text: 'Open it for cash payments, cash refunds and paid in/out' })),
        choice.commands === 'StarGraphic' ? null : seg('Drawer socket', [[2, 'Pin 2 (usual)'], [5, 'Pin 5']], choice.drawerPin, v => { choice.drawerPin = v; })) : null);
  };
  render();
  return card;
}

/** What a kitchen printer prints: everything, or chosen categories and single items. */
function printsWhat(k, label, menu) {
  const box = h('div', { class: 'prints-what' });
  const render = () => {
    const toggle = (list, id) => { const i = list.indexOf(id); if (i >= 0) list.splice(i, 1); else list.push(id); render(); };
    k.excludedProductIds ??= [];
    // A single item: as its category → "also here" → "not here" → as its category.
    const cycle = id => {
      if (k.productIds.includes(id)) { k.productIds.splice(k.productIds.indexOf(id), 1); k.excludedProductIds.push(id); }
      else if (k.excludedProductIds.includes(id)) k.excludedProductIds.splice(k.excludedProductIds.indexOf(id), 1);
      else k.productIds.push(id);
      render();
    };
    const state = id => (k.productIds.includes(id) ? 'also' : k.excludedProductIds.includes(id) ? 'not' : '');
    const chosenItems = k.productIds.length + k.excludedProductIds.length;
    mount(box,
      h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Prints' }),
        h('div', { class: 'seg seg-small', role: 'radiogroup', 'aria-label': `${label}: prints` }, [['Everything', 'Everything'], ['Chosen', 'Only what I choose']].map(([v, text]) => h('button', {
          type: 'button', class: `seg-btn ${k.mode === v ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(k.mode === v),
          onclick: () => { k.mode = v; render(); }, text,
        })))),
      k.mode === 'Chosen' ? [
        h('span', { class: 'field-label', text: 'Categories' }),
        h('div', { class: 'chips', role: 'group', 'aria-label': `${label}: categories` }, menu.categories.map(c => h('button', {
          type: 'button', class: `chip ${k.categoryIds.includes(c.id) ? 'chip-on' : ''}`, 'aria-pressed': String(k.categoryIds.includes(c.id)),
          onclick: () => toggle(k.categoryIds, c.id), text: c.name.en,
        }))),
        h('details', { class: 'single-items', open: chosenItems > 0 },
          h('summary', { text: chosenItems ? `Single items (${k.productIds.length} also here, ${k.excludedProductIds.length} not here)` : 'Single items: print one item here as well, or not here' }),
          h('p', { class: 'field-hint', text: 'Tap an item: ＋ also print it here (it still prints on its category\'s printer too) · ✕ do not print it here · tap again to go back to its category.' }),
          menu.categories.map(c => {
            const items = menu.products.filter(p => p.categoryId === c.id);
            return items.length ? h('div', { class: 'item-group' }, h('span', { class: 'field-hint', text: c.name.en }),
              h('div', { class: 'chips', role: 'group', 'aria-label': `${label}: ${c.name.en} items` }, items.map(p => {
                const st = state(p.id);
                return h('button', {
                  type: 'button', class: `chip chip-small ${st === 'also' ? 'chip-also' : st === 'not' ? 'chip-not' : ''}`,
                  'aria-label': `${p.name.en}${st === 'also' ? ': also here' : st === 'not' ? ': not here' : ''}`,
                  onclick: () => cycle(p.id), text: `${st === 'also' ? '＋ ' : st === 'not' ? '✕ ' : ''}${p.name.en}`,
                });
              }))) : null;
          })),
      ] : null);
  };
  render();
  return box;
}

/** More → Printers (owner). */
export async function printersDialog() {
  let data;
  let menu;
  try {
    [data, menu] = await busy(() => Promise.all([api.get('/api/settings/printers'), api.get('/api/menu')]));
  } catch (e) {
    toast(problemText(e), 'error');
    return;
  }

  menu = {
    categories: [...menu.categories].filter(c => !c.isHidden).sort((a, b) => a.sortOrder - b.sortOrder),
    products: [...menu.products].sort((a, b) => a.sortOrder - b.sortOrder),
  };
  const setup = structuredClone(data.setup);
  await openDialog(close => {
    const kitchenList = h('div', { class: 'kitchen-list' });
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
    const testKitchen = index => run(async () => {
      await save(); // a new printer gets its id when saved
      const result = await busy(() => api.post('/api/settings/printers/test', { target: 'Kitchen', kitchenId: setup.kitchens[index].id }));
      toast(`Test page sent to ${result.output}.`, 'ok', 6000);
      renderKitchens();
    });
    const renderKitchens = () => mount(kitchenList,
      setup.kitchens.map((k, i) => {
        const label = `Kitchen printer ${i + 1}`;
        const name = h('input', { class: 'text-input', type: 'text', maxlength: 30, value: k.name, 'aria-label': `${label}: name`, placeholder: 'e.g. Pizza' });
        name.addEventListener('input', () => { k.name = name.value; });
        return h('section', { class: 'kitchen-card', 'aria-label': label },
          h('div', { class: 'kitchen-head' },
            h('label', { class: 'field kitchen-name' }, h('span', { class: 'field-label', text: `${label} (name on its tickets)` }), name),
            setup.kitchens.length > 1 ? h('button', { class: 'btn btn-small btn-danger-soft', onclick: () => { setup.kitchens.splice(i, 1); renderKitchens(); }, text: 'Remove' }) : null),
          i === 0 && setup.kitchens.length > 1 ? h('p', { class: 'field-hint', text: 'The first printer also prints anything no other printer has chosen, so nothing is missed.' }) : null,
          printsWhat(k, label, menu),
          printerCard(`${label} connection`, k.printer, data.installed, false, setup),
          h('button', { class: 'btn btn-small', onclick: testKitchen(i), text: `Test ${k.name || 'this printer'}` }));
      }),
      setup.kitchens.length < 10 ? h('button', {
        class: 'btn', text: '+ Add kitchen printer',
        onclick: () => {
          setup.kitchens.push({ id: '00000000-0000-0000-0000-000000000000', name: '', printer: structuredClone(setup.kitchens[0].printer), mode: 'Chosen', categoryIds: [], productIds: [], excludedProductIds: [] });
          renderKitchens();
          kitchenList.querySelectorAll('.kitchen-name input')[setup.kitchens.length - 1]?.focus();
        },
      }) : h('p', { class: 'field-hint', text: 'At most 10 kitchen printers.' }));
    renderKitchens();
    return {
      title: 'Printers',
      wide: true,
      class: 'dialog-printers',
      body: h('div', { class: 'printers' },
        h('div', { class: 'printers-col' },
          printerCard('Receipt printer', setup.receipt, data.installed, true, setup),
          h('div', { class: 'printer-tests' },
            h('button', { class: 'btn btn-small', onclick: run(async () => { await save(); const r = await busy(() => api.post('/api/settings/printers/test', { target: 'Receipt' })); toast(`Test page sent to ${r.output}.`, 'ok', 6000); }), text: 'Test receipt printer' }),
            h('button', { class: 'btn btn-small', onclick: run(async () => { await save(); await busy(() => api.post('/api/settings/printers/test-drawer')); toast('Drawer opened.', 'ok'); }), text: 'Test drawer' }))),
        h('div', { class: 'printers-col' },
          h('h3', { class: 'menu-heading', text: 'Kitchen printers' }),
          h('p', { class: 'field-hint', text: 'Each kitchen printer prints only the food it is set for, e.g. Pizzas on the pizza printer, Drinks on the drinks printer. One item can print on several printers. A meal deal is split between them.' }),
          kitchenList)),
      actions: [
        h('button', { class: 'btn', onclick: () => close(false), text: 'Cancel' }),
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
