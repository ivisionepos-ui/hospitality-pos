// More → Receipt layouts (owner): for the customer receipt, the delivery receipt and the kitchen ticket choose which
// parts print, their order, the item text size, the language and your own text at the top and bottom, with a preview
// of the newest order drawn exactly as the printer will print it.
import { api, ApiError } from './api.js';
import { h, mount, toast } from './dom.js';
import { busy, openDialog } from './dialogs.js';

const KINDS = [['Receipt', 'receipt', 'Customer receipt'], ['Delivery', 'delivery', 'Delivery receipt'], ['Kitchen', 'kitchen', 'Kitchen ticket']];
const LANGUAGES = [['', 'Shop setting'], ['en', 'English'], ['ar', 'Arabic'], ['both', 'Both']];
const SIZES = [['Normal', 'Normal'], ['Large', 'Large'], ['Huge', 'Huge']];

export async function receiptLayoutsDialog() {
  let setup;
  try {
    setup = await busy(() => api.get('/api/settings/receipt-layouts'));
  } catch (e) {
    toast(e.message ?? 'Could not reach the hub.', 'error');
    return;
  }

  const layouts = structuredClone(setup.layouts);
  let tab = 'Receipt';
  let previewUrl = null;
  let previewTimer = null;
  let previewSeq = 0;

  const key = kind => KINDS.find(k => k[0] === kind)[1];
  const catalogue = kind => (kind === 'Kitchen' ? setup.kitchenParts : setup.receiptParts);
  const required = (kind, id) => {
    const info = catalogue(kind).find(p => p.id === id);
    return !!info?.required && (kind === 'Kitchen' || setup.vatRegistered || !['vatNumber', 'vatSummary', 'address'].includes(id));
  };

  await openDialog(close => {
    const tabs = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Printout' });
    const editor = h('div', { class: 'rl-editor' });
    const preview = h('div', { class: 'rl-preview', 'aria-live': 'polite' });

    const refreshPreview = () => {
      clearTimeout(previewTimer);
      previewTimer = setTimeout(async () => {
        const seq = ++previewSeq;
        try {
          const response = await api.post(`/api/settings/receipt-layouts/preview?kind=${tab}`, layouts[key(tab)], { raw: true });
          const blob = await response.blob();
          if (seq !== previewSeq) return; // a newer change is on its way
          if (previewUrl) URL.revokeObjectURL(previewUrl);
          previewUrl = URL.createObjectURL(blob);
          mount(preview, h('img', { src: previewUrl, alt: `Preview of the ${KINDS.find(k => k[0] === tab)[2].toLowerCase()}` }));
        } catch (e) {
          if (seq !== previewSeq) return;
          mount(preview, h('p', { class: 'empty-note', text: e instanceof ApiError ? e.message : 'The preview could not be drawn.' }));
        }
      }, 250);
    };

    const boxes = new Map(); // part id → its tick box, so typing text can tick it without redrawing
    const render = () => {
      const layout = layouts[key(tab)];
      const scrolled = editor.scrollTop;
      boxes.clear();
      mount(tabs, KINDS.map(([kind, , label]) => h('button', {
        type: 'button', role: 'tab', class: `seg-btn ${tab === kind ? 'seg-on' : ''}`, 'aria-selected': String(tab === kind),
        onclick: () => { if (tab !== kind) { tab = kind; editor.scrollTop = 0; render(); } }, text: label,
      })));

      const change = fn => { fn(layout); render(); };
      const move = (i, by) => change(l => {
        const parts = [...l.parts];
        const [part] = parts.splice(i, 1);
        parts.splice(i + by, 0, part);
        l.parts = parts;
      });

      const list = h('ol', { class: 'rl-parts' }, layout.parts.map((part, i) => {
        const info = catalogue(tab).find(p => p.id === part.id);
        const locked = required(tab, part.id);
        return h('li', { class: `rl-part ${part.show || locked ? '' : 'rl-off'}` },
          h('label', { class: 'check' },
            (() => {
              const box = h('input', { type: 'checkbox', checked: part.show || locked, disabled: locked, onchange: e => change(() => { part.show = e.target.checked; }) });
              boxes.set(part.id, box);
              return box;
            })(),
            h('span', { text: info?.label ?? part.id }),
            locked ? h('small', { class: 'rl-required', text: tab === 'Kitchen' ? 'always' : 'required by law' }) : null),
          h('span', { class: 'rl-move' },
            h('button', { type: 'button', class: 'btn btn-small', 'aria-label': `Move ${info?.label} up`, disabled: i === 0, onclick: () => move(i, -1), text: '▲' }),
            h('button', { type: 'button', class: 'btn btn-small', 'aria-label': `Move ${info?.label} down`, disabled: i === layout.parts.length - 1, onclick: () => move(i, 1), text: '▼' })));
      }));

      const choose = (label, options, current, set) => h('div', { class: 'field' }, h('span', { class: 'field-label', text: label }),
        h('div', { class: 'seg seg-small', role: 'radiogroup', 'aria-label': label }, options.map(([value, text]) => h('button', {
          type: 'button', role: 'radio', class: `seg-btn ${current === value ? 'seg-on' : ''}`, 'aria-checked': String(current === value),
          onclick: () => change(() => set(value)), text,
        }))));

      const textBox = (label, prop, partId) => {
        const box = h('textarea', { class: 'text-input', rows: 2, maxlength: 200, 'aria-label': label, placeholder: prop === 'topText' ? 'e.g. Order online at …' : 'e.g. 10% off your next order with this receipt' });
        box.value = layout[prop] ?? '';
        box.addEventListener('input', () => {
          layout[prop] = box.value;
          const part = layout.parts.find(p => p.id === partId);
          if (part && box.value.trim() && !part.show) { // typing turns the part on (without redrawing: the cursor stays)
            part.show = true;
            const tick = boxes.get(partId);
            if (tick) { tick.checked = true; tick.closest('.rl-part')?.classList.remove('rl-off'); }
          }

          refreshPreview();
        });
        return h('label', { class: 'field' }, h('span', { class: 'field-label', text: label }), box);
      };

      mount(editor,
        h('p', { class: 'field-hint', text: 'Tick the parts to print and use ▲ ▼ to change their order. Dashed lines are added between groups by themselves.' }),
        list,
        choose('Item text size', SIZES, layout.itemSize, v => { layout.itemSize = v; }),
        choose('Language of item names', LANGUAGES, layout.language ?? '', v => { layout.language = v || null; }),
        textBox('Your text at the top', 'topText', 'topText'),
        textBox('Your text at the bottom', 'bottomText', 'bottomText'),
        h('button', { type: 'button', class: 'btn', onclick: () => change(() => { layouts[key(tab)] = structuredClone(setup.standard[key(tab)]); }), text: 'Back to the standard layout' }));
      editor.scrollTop = scrolled;
      refreshPreview();
    };

    const save = async () => {
      try {
        await busy(() => api.put('/api/settings/receipt-layouts', layouts));
        toast('Receipt layouts saved. New printouts use them.', 'ok', 5000);
        close();
      } catch (e) {
        toast(e.message, 'error', 8000);
      }
    };

    render();
    return {
      title: 'Receipt layouts',
      wide: true,
      class: 'dialog-receipt-layouts',
      body: [tabs, h('div', { class: 'rl-body' }, editor, h('div', { class: 'rl-preview-wrap' }, h('span', { class: 'field-label', text: 'Preview (newest order)' }), preview))],
      actions: [h('button', { class: 'btn', onclick: () => close(), text: 'Cancel' }), h('button', { class: 'btn btn-primary', onclick: save, text: 'Save' })],
    };
  });

  if (previewUrl) URL.revokeObjectURL(previewUrl);
}
