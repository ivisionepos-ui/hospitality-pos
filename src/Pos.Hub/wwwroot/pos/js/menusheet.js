// More → Menu spreadsheet: download the menu as an Excel file, fill it in, and import it back.
// Nothing changes until the preview shows no problems and Import is pressed; a backup is made first.
import { api, ApiError } from './api.js';
import { h, mount, toast } from './dom.js';
import { busy, confirmDialog, openDialog } from './dialogs.js';

const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

async function download() {
  try {
    const blob = await busy(() => api.blob('/api/menu/spreadsheet'));
    const url = URL.createObjectURL(blob);
    const link = h('a', { href: url, download: 'menu.xlsx' });
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    toast('menu.xlsx saved to Downloads. Open it in Excel, fill it in, save, then choose it here.', 'ok', 6000);
  } catch (e) {
    toast(e instanceof ApiError ? e.message : 'Could not save the spreadsheet.', 'error');
  }
}

function summary(p) {
  const kpi = (label, value) => h('div', { class: 'kpi' }, h('span', { class: 'kpi-label', text: label }), h('strong', { class: 'kpi-value', text: value }));
  return h('div', { class: 'kpis' },
    kpi('Items in the sheet', String(p.items)),
    kpi('New items', String(p.newItems)),
    kpi('Changed items', String(p.changedItems)),
    kpi('New categories', String(p.newCategories)));
}

function problemsList(problems) {
  return h('div', { class: 'import-problems', role: 'alert' },
    h('h3', { text: `${plural(problems.length, 'problem')} to fix in the spreadsheet` }),
    h('p', { class: 'field-hint', text: 'Nothing has been changed. Fix these rows in Excel, save, and choose the file again.' }),
    h('ul', {}, problems.slice(0, 200).map(x => h('li', {},
      x.row ? h('strong', { text: `${x.sheet} row ${x.row}: ` }) : null, x.message))),
    problems.length > 200 ? h('p', { class: 'field-hint', text: `…and ${problems.length - 200} more.` }) : null);
}

function changesList(p) {
  const notes = [];
  if (p.unchangedItems) notes.push(`${plural(p.unchangedItems, 'item')} already the same.`);
  if (p.notInSheet && !p.hidden) notes.push(`${plural(p.notInSheet, 'item')} on the till ${p.notInSheet === 1 ? 'is' : 'are'} not in the sheet and will be left as ${p.notInSheet === 1 ? 'it is' : 'they are'}.`);
  if (p.newExtrasGroups) notes.push(`${plural(p.newExtrasGroups, 'new extras group')}.`);
  if (p.newCategories) notes.push('If your kitchen printers print chosen categories only, add the new categories to them in Printers.');
  return [
    notes.length ? h('p', { class: 'field-hint', text: notes.join(' ') }) : null,
    p.changes.length
      ? h('ul', { class: 'import-changes', 'aria-label': 'What will change' }, p.changes.map(c => h('li', { text: c })))
      : h('p', { class: 'empty-note', text: 'The spreadsheet matches the menu already: nothing to change.' }),
  ];
}

/** More → Menu spreadsheet. */
export async function menuSpreadsheetDialog() {
  let last = null;
  try {
    last = await busy(() => api.get('/api/menu/spreadsheet/last'));
  } catch (e) {
    toast(e instanceof ApiError ? e.message : 'Could not reach the hub.', 'error');
    return;
  }

  let file = null;
  let preview = null;
  let missing = 'keep';

  await openDialog(close => {
    const body = h('div', { class: 'menu-sheet' });
    const picker = h('input', { type: 'file', accept: '.xlsx,.csv', class: 'file-input', 'aria-label': 'Choose a spreadsheet' });
    const importBtn = h('button', { class: 'btn btn-primary', text: 'Import', disabled: true });

    const check = async () => {
      if (!file) return;
      try {
        preview = await busy(() => api.upload(`/api/menu/spreadsheet/preview?name=${encodeURIComponent(file.name)}&missing=${missing}`, file));
      } catch (e) {
        preview = null;
        toast(e instanceof ApiError ? e.message : 'Could not read the file.', 'error', 8000);
      }

      render();
    };

    const render = () => {
      importBtn.disabled = !(preview?.canImport);
      mount(body,
        h('ol', { class: 'steps' },
          h('li', {}, h('button', { class: 'btn', onclick: download, text: 'Download the menu spreadsheet' }),
            h('span', { class: 'field-hint', text: ' Your menu as it is now (or examples if it is empty), with a How-to sheet.' })),
          h('li', { text: 'Fill it in with Excel: one row for each size of each item.' }),
          h('li', {}, h('label', { class: 'btn btn-primary file-btn' }, file ? 'Choose another file…' : 'Choose the filled-in file…', picker),
            file ? h('span', { class: 'chosen-file', text: ` ${file.name}` }) : null)),
        h('div', { class: 'field' },
          h('span', { class: 'field-label', text: 'Items on the till that are not in the spreadsheet' }),
          h('div', { class: 'seg seg-small', role: 'radiogroup' }, [['keep', 'Leave them as they are'], ['hide', 'Hide them']].map(([id, label]) => h('button', {
            class: `seg-btn ${missing === id ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(missing === id), text: label,
            onclick: async () => { missing = id; render(); await check(); },
          }))),
          h('span', { class: 'field-hint', text: 'Hidden items are kept (with their sales history) and can be shown again in Edit menu.' })),
        preview ? h('section', { class: 'import-preview', 'aria-label': 'Preview' },
          summary(preview),
          preview.problems.length ? problemsList(preview.problems) : changesList(preview)) : null,
        last ? h('section', { class: 'last-import' },
          h('h3', { text: 'Last import' }),
          h('p', { text: `${last.fileName} · ${new Date(last.at).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })} · ${last.summary}` }),
          last.canUndo
            ? h('button', { class: 'btn btn-danger', text: 'Undo last import', onclick: undo })
            : h('p', { class: 'field-hint', text: 'The menu has been changed since, so this import can no longer be undone in one step. A backup was made before it.' })) : null);
    };

    const undo = async () => {
      if (!await confirmDialog('Undo last import?', `The menu goes back to exactly how it was before ${last.fileName} was imported. A backup is made first.`, 'Undo import', true)) return;
      try {
        await busy(() => api.post('/api/menu/spreadsheet/undo'));
        toast('The menu is back to how it was before the import.', 'ok');
        last = null;
        preview = null;
        render();
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Could not undo the import.', 'error', 8000);
      }
    };

    importBtn.addEventListener('click', async () => {
      if (!file || !preview?.canImport) return;
      try {
        const done = await busy(() => api.upload(`/api/menu/spreadsheet/import?name=${encodeURIComponent(file.name)}&missing=${missing}`, file));
        toast(`Menu imported: ${plural(done.newItems, 'new item')}, ${plural(done.changedItems, 'changed item')}${done.hidden ? `, ${done.hidden} hidden` : ''}. A backup was made first.`, 'ok', 7000);
        close(true);
      } catch (e) {
        toast(e instanceof ApiError ? e.message : 'Could not import the menu.', 'error', 8000);
        await check();
      }
    });

    picker.addEventListener('change', async () => {
      file = picker.files[0] ?? null;
      picker.value = '';
      await check();
    });

    render();
    return {
      title: 'Menu spreadsheet',
      wide: true,
      class: 'dialog-menu-sheet',
      body,
      actions: [h('button', { class: 'btn', onclick: () => close(), text: 'Close' }), importBtn],
    };
  });
}
