// Edit menu mode: the same layout as taking orders. Tap an item (or a category tab) to change it on the left;
// changes are saved to the hub and appear on every till straight away.
import { api, ApiError } from './api.js';
import { h, mount, newId, penceText, text, timeText, toast } from './dom.js';
import { amountDialog, busy, confirmDialog, openDialog, textDialog } from './dialogs.js';
import { MenuView } from './menu.js';

const VAT = [
  { id: 'HotFood', label: 'Hot food', help: '20% VAT on everything' },
  { id: 'ColdFood', label: 'Cold food', help: '0% when taken away, 20% eaten in' },
  { id: 'AlwaysStandard', label: 'Always 20%', help: 'Drinks, sweets, crisps, ice cream' },
];
const COLOURS = ['#7c3aed', '#0f766e', '#c2410c', '#b91c1c', '#a16207', '#4d7c0f', '#be185d', '#1d4ed8', '#0e7490', '#374151'];

const clone = value => structuredClone(value);
const lt = en => ({ en });
const money = pence => ({ minorUnits: pence, currency: 'GBP' });

function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', { class: 'field-label', text: label }), input, hint ? h('span', { class: 'field-hint', text: hint }) : null);
}

function toggle(label, on, change) {
  return h('button', { class: `switch ${on ? 'switch-on' : ''}`, role: 'switch', 'aria-checked': String(on), onclick: () => change(!on) },
    h('span', { class: 'switch-knob' }), label);
}

function colourPicker(current, change) {
  return h('div', { class: 'swatches', role: 'radiogroup', 'aria-label': 'Colour' }, COLOURS.map(c => h('button', {
    class: `swatch ${current === c ? 'swatch-on' : ''}`, style: { '--swatch': c }, role: 'radio', 'aria-checked': String(current === c), 'aria-label': c,
    onclick: () => change(c),
  })));
}

export class MenuEditor {
  /** @param {import('./till.js').Till} till */
  constructor(till) {
    this.till = till;
    this.panel = null; // { kind: 'product' | 'category', draft, original, isNew }
  }

  get menu() { return this.till.menu; }

  /** Reloads the menu after a change (ours or another till's). */
  async reload() {
    await this.till.loadMenu();
    this.render();
  }

  render() {
    this.renderCatalog();
    this.renderPanel();
  }

  // ---------- Right: tabs and buttons, as on the order screen ----------

  renderCatalog() {
    const el = this.till.catalogEl;
    const categories = this.menu.state.categories.slice().sort((a, b) => a.sortOrder - b.sortOrder);
    if (!categories.some(c => c.id === this.till.categoryId)) this.till.categoryId = categories[0]?.id ?? null;
    const tabs = h('nav', { class: 'cats', 'aria-label': 'Categories' },
      categories.map(c => h('button', {
        class: `cat ${c.id === this.till.categoryId ? 'cat-on' : ''} ${c.isHidden ? 'cat-hidden' : ''}`,
        style: c.colour ? { '--cat': c.colour } : null,
        onclick: () => {
          if (c.id === this.till.categoryId) this.openCategory(c);
          else { this.till.categoryId = c.id; this.renderCatalog(); }
        },
      }, text(c.name), c.id === this.till.categoryId ? h('span', { class: 'cat-edit', 'aria-hidden': 'true', text: ' ✎' }) : null)),
      h('button', { class: 'cat cat-add', onclick: () => this.addCategory(), text: '+ Category' }));

    const category = categories.find(c => c.id === this.till.categoryId);
    const selectedId = this.panel?.kind === 'product' ? this.panel.draft.id : null;
    const items = category ? this.menu.productsIn(category.id, true) : [];
    const grid = h('div', { class: 'items' },
      items.map(p => h('button', {
        class: `item ${selectedId === p.id ? 'item-selected' : ''} ${p.isHidden ? 'item-hidden' : ''} ${this.menu.isSoldOut(p) ? 'item-out' : ''}`,
        style: category.colour ? { '--cat': category.colour } : null,
        onclick: () => this.openProduct(p),
      }, h('span', { class: 'item-name', text: this.menu.buttonName(p) }),
        h('span', { class: 'item-price', text: [p.variants.map(v => penceText(v.price.minorUnits)).join(' / '), p.isHidden ? 'Hidden' : null, this.menu.isSoldOut(p) ? 'Sold out' : null].filter(Boolean).join(' · ') }))),
      category ? h('button', { class: 'item item-add', onclick: () => this.addProduct(category), text: '+ Add item' }) : null);

    mount(this.till.catalogEl, tabs, category ? grid : h('div', { class: 'empty' }, h('p', { text: 'Add a category to start.' })));
  }

  // ---------- Left: the edit panel ----------

  renderPanel() {
    const el = this.till.ticketEl;
    if (!this.panel) {
      mount(el, h('div', { class: 'edit-hint' },
        h('h2', { text: 'Edit menu' }),
        h('p', { text: 'Tap an item to change its name, prices, extras or sold out. Tap the selected category tab to change the category. Use + to add.' }),
        h('p', { text: 'Every change is saved straight away and shows on all tills. Undo takes back the last change.' })));
      return;
    }

    if (this.panel.kind === 'category') this.categoryPanel(el);
    else this.productPanel(el);
  }

  openProduct(product) {
    if (this.panel && this.isDirty()) {
      confirmDialog('Leave without saving?', 'Your changes to this item have not been saved.', 'Leave').then(ok => {
        if (ok) { this.panel = null; this.openProduct(product); }
      });
      return;
    }

    this.panel = { kind: 'product', draft: clone(product), original: clone(product), isNew: false };
    this.render();
  }

  openCategory(category) {
    this.panel = { kind: 'category', draft: clone(category), original: clone(category), isNew: false };
    this.render();
  }

  isDirty() {
    return this.panel && JSON.stringify(this.panel.draft) !== JSON.stringify(this.panel.original);
  }

  close() {
    this.panel = null;
    this.render();
  }

  productPanel(el) {
    const { draft, isNew } = this.panel;
    const isDeal = draft.type === 'MealDeal';
    const rerender = () => this.renderPanel();
    const problem = h('p', { class: 'form-problem', role: 'alert' });

    const name = h('input', { class: 'text-input', type: 'text', maxlength: 120, value: text(draft.name) });
    name.addEventListener('input', () => { draft.name = lt(name.value); });
    const button = h('input', { class: 'text-input', type: 'text', maxlength: 120, value: draft.buttonName ? text(draft.buttonName) : '', placeholder: 'Same as the name' });
    button.addEventListener('input', () => { draft.buttonName = button.value.trim() ? lt(button.value) : null; });

    const sizes = h('div', { class: 'sizes' },
      draft.variants.map((v, i) => {
        const size = h('input', { class: 'text-input size-name', type: 'text', maxlength: 40, value: text(v.name), 'aria-label': `Size ${i + 1} name` });
        const price = h('button', {
          class: 'price-btn', 'aria-label': `Price of ${text(v.name)}`,
          onclick: async () => {
            const pence = await amountDialog(`Price — ${text(v.name)}`, { initial: v.price.minorUnits, ok: 'Set price', allowZero: true });
            if (pence !== null) { v.price = money(pence); rerender(); }
          },
        }, penceText(v.price.minorUnits));
        size.addEventListener('input', () => { v.name = lt(size.value); price.setAttribute('aria-label', `Price of ${size.value}`); });
        return h('div', { class: 'size-row' },
          draft.variants.length > 1 || !isDeal ? size : null,
          price,
          draft.variants.length > 1 ? h('button', { class: 'icon-btn', 'aria-label': `Remove size ${text(v.name)}`, onclick: () => { draft.variants.splice(i, 1); rerender(); }, text: '✕' }) : null);
      }),
      isDeal ? null : h('button', { class: 'btn btn-small', onclick: () => { draft.variants.push({ id: newId(), name: lt(draft.variants.length === 1 ? 'Large' : 'Size'), price: money(draft.variants.at(-1).price.minorUnits), isSoldOut: false }); rerender(); }, text: '+ Add size' }));

    const vat = h('div', { class: 'seg seg-small seg-wrap' }, VAT.map(v => h('button', {
      class: `seg-btn ${draft.vatCategory === v.id ? 'seg-on' : ''}`, 'aria-pressed': String(draft.vatCategory === v.id),
      onclick: () => { draft.vatCategory = v.id; rerender(); }, text: v.label,
    })));

    const groups = this.menu.state.modifierGroups;
    const extras = isDeal ? null : h('div', { class: 'chips' },
      groups.map(g => {
        const on = draft.modifierGroupIds.includes(g.id);
        return h('span', { class: 'chip-pair' },
          h('button', {
            class: `chip ${on ? 'chip-on' : ''}`, 'aria-pressed': String(on),
            onclick: () => { draft.modifierGroupIds = on ? draft.modifierGroupIds.filter(id => id !== g.id) : [...draft.modifierGroupIds, g.id]; rerender(); },
          }, on ? '✓ ' : '', text(g.name)),
          h('button', { class: 'chip chip-edit', 'aria-label': `Edit ${text(g.name)}`, onclick: () => this.editExtras(g), text: '✎' }));
      }),
      h('button', { class: 'chip', onclick: () => this.editExtras(null, created => { draft.modifierGroupIds = [...draft.modifierGroupIds, created.id]; }), text: '+ New extras' }));

    const categories = this.menu.state.categories.slice().sort((a, b) => a.sortOrder - b.sortOrder);
    const siblings = this.menu.productsIn(draft.categoryId, true);
    const index = siblings.findIndex(p => p.id === draft.id);
    const move = isNew ? null : h('div', { class: 'move-row' },
      h('button', { class: 'btn btn-small', disabled: index <= 0, onclick: () => this.moveProduct(index - 1), text: '◀ Earlier' }),
      h('button', { class: 'btn btn-small', disabled: index >= siblings.length - 1, onclick: () => this.moveProduct(index + 1), text: 'Later ▶' }),
      (() => {
        const select = h('select', { class: 'select', 'aria-label': 'Move to category' },
          categories.map(c => h('option', { value: c.id, selected: c.id === draft.categoryId, text: text(c.name) })));
        select.addEventListener('change', () => this.moveProduct(0, select.value));
        return select;
      })());

    mount(el, h('div', { class: 'edit-panel' },
      h('div', { class: 'edit-head' }, h('h2', { text: isNew ? 'New item' : `Edit ${text(this.panel.original.name)}` }),
        h('button', { class: 'icon-btn', 'aria-label': 'Close', onclick: () => this.closeWithCheck(), text: '✕' })),
      h('div', { class: 'edit-body' },
        field('Name (on receipts and kitchen tickets)', name),
        field('Button name (optional, shorter)', button),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: isDeal ? 'Deal price' : 'Sizes and prices' }), sizes),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'VAT' }), vat,
          h('span', { class: 'field-hint', text: VAT.find(v => v.id === draft.vatCategory)?.help ?? '' })),
        isDeal ? h('p', { class: 'field-hint', text: `Deal choices: ${draft.dealSlots.map(s => text(s.name)).join(', ')}. Changing the choices comes with the back office.` }) : null,
        extras ? h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Extras offered with this item' }), extras) : null,
        h('div', { class: 'switch-row' },
          toggle('Sold out', draft.isSoldOut, v => { draft.isSoldOut = v; rerender(); }),
          toggle('Hidden from tills', draft.isHidden, v => { draft.isHidden = v; rerender(); })),
        move ? h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Position' }), move) : null,
        problem),
      h('div', { class: 'edit-actions' },
        h('button', { class: 'btn', onclick: () => this.closeWithCheck(), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: () => this.saveProduct(problem), text: isNew ? 'Add item' : 'Save' }))));
  }

  async closeWithCheck() {
    if (this.isDirty() && !(await confirmDialog('Leave without saving?', 'Your changes have not been saved.', 'Leave'))) return;
    this.close();
  }

  async saveProduct(problem) {
    const { draft, original, isNew } = this.panel;
    draft.name = lt(text(draft.name).trim());
    if (!text(draft.name)) { problem.textContent = 'Enter a name.'; return; }
    if (draft.variants.some(v => !text(v.name).trim())) { problem.textContent = 'Every size needs a name.'; return; }
    try {
      await busy(() => (isNew
        ? api.post('/api/menu/products', { product: draft })
        : api.put(`/api/menu/products/${draft.id}`, { product: draft, original })));
      toast(`${text(draft.name)} saved. All tills updated.`, 'ok');
      await this.till.loadMenu();
      const saved = this.menu.products.get(draft.id);
      this.panel = saved ? { kind: 'product', draft: clone(saved), original: clone(saved), isNew: false } : null;
      this.render();
    } catch (e) {
      problem.textContent = e.message;
      if (e instanceof ApiError && e.code === 'ChangedElsewhere') {
        await this.till.loadMenu();
        this.renderCatalog();
      }
    }
  }

  async moveProduct(position, categoryId) {
    const { draft } = this.panel;
    if (this.isDirty()) { toast('Save or cancel your changes first, then move it.', 'info'); return; }
    try {
      await busy(() => api.post(`/api/menu/products/${draft.id}/move`, { position, categoryId: categoryId ?? null }));
      if (categoryId) this.till.categoryId = categoryId;
      await this.till.loadMenu();
      const moved = this.menu.products.get(draft.id);
      this.panel = { kind: 'product', draft: clone(moved), original: clone(moved), isNew: false };
      this.render();
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  addProduct(category) {
    const product = {
      id: newId(), categoryId: category.id, name: lt(''), buttonName: null, type: 'Standard',
      variants: [{ id: newId(), name: lt('Regular'), price: money(0), isSoldOut: false }],
      vatCategory: 'HotFood', isSoldOut: false, isHidden: false, modifierGroupIds: [], dealSlots: [], allergens: [],
    };
    this.panel = { kind: 'product', draft: product, original: clone(product), isNew: true };
    this.render();
    this.till.ticketEl.querySelector('input')?.focus();
  }

  // ---------- Categories ----------

  categoryPanel(el) {
    const { draft, original } = this.panel;
    const rerender = () => this.renderPanel();
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    const name = h('input', { class: 'text-input', type: 'text', maxlength: 60, value: text(draft.name) });
    name.addEventListener('input', () => { draft.name = lt(name.value); });
    const groups = this.menu.state.courseGroups.slice().sort((a, b) => a.sortOrder - b.sortOrder);
    const course = h('div', { class: 'seg seg-small seg-wrap' },
      [{ id: null, name: lt('None') }, ...groups].map(g => h('button', {
        class: `seg-btn ${(draft.courseGroupId ?? null) === g.id ? 'seg-on' : ''}`,
        onclick: () => { draft.courseGroupId = g.id; rerender(); }, text: text(g.name),
      })));
    const tabs = this.menu.state.categories.slice().sort((a, b) => a.sortOrder - b.sortOrder);
    const index = tabs.findIndex(c => c.id === draft.id);

    mount(el, h('div', { class: 'edit-panel' },
      h('div', { class: 'edit-head' }, h('h2', { text: `Category ${text(original.name)}` }),
        h('button', { class: 'icon-btn', 'aria-label': 'Close', onclick: () => this.closeWithCheck(), text: '✕' })),
      h('div', { class: 'edit-body' },
        field('Name', name),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Colour' }), colourPicker(draft.colour, c => { draft.colour = c; rerender(); })),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Food type (sets the order on receipts)' }), course),
        h('div', { class: 'switch-row' }, toggle('Hidden from tills', draft.isHidden, v => { draft.isHidden = v; rerender(); })),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Position' }), h('div', { class: 'move-row' },
          h('button', { class: 'btn btn-small', disabled: index <= 0, onclick: () => this.moveCategory(index - 1), text: '◀ Earlier' }),
          h('button', { class: 'btn btn-small', disabled: index >= tabs.length - 1, onclick: () => this.moveCategory(index + 1), text: 'Later ▶' }))),
        problem),
      h('div', { class: 'edit-actions' },
        h('button', { class: 'btn', onclick: () => this.closeWithCheck(), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: () => this.saveCategory(problem), text: 'Save' }))));
  }

  async saveCategory(problem) {
    const { draft, original } = this.panel;
    if (!text(draft.name).trim()) { problem.textContent = 'Enter a name.'; return; }
    try {
      await busy(() => api.put(`/api/menu/categories/${draft.id}`, { category: draft, original }));
      toast(`${text(draft.name)} saved.`, 'ok');
      await this.till.loadMenu();
      this.close();
    } catch (e) {
      problem.textContent = e.message;
    }
  }

  async moveCategory(position) {
    if (this.isDirty()) { toast('Save or cancel your changes first, then move it.', 'info'); return; }
    try {
      await busy(() => api.post(`/api/menu/categories/${this.panel.draft.id}/move`, { position }));
      await this.till.loadMenu();
      this.openCategory(this.menu.state.categories.find(c => c.id === this.panel.draft.id));
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  async addCategory() {
    const name = await textDialog('New category', { label: 'Name, e.g. Wraps', ok: 'Add', maxLength: 60 });
    if (!name) return;
    const category = { id: newId(), name: lt(name), colour: COLOURS[this.menu.state.categories.length % COLOURS.length], isHidden: false, sortOrder: 0, courseGroupId: null };
    try {
      await busy(() => api.post('/api/menu/categories', category));
      this.till.categoryId = category.id;
      await this.till.loadMenu();
      this.openCategory(this.menu.state.categories.find(c => c.id === category.id));
      toast(`${name} added. Now add its items with + Add item.`, 'ok');
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  // ---------- Extras (sauces, toppings…) ----------

  /** Edits a group of extras (or makes a new one). onCreated is called with a new group once saved. */
  async editExtras(group, onCreated) {
    const isNew = !group;
    const original = group ? clone(group) : null;
    const draft = group ? clone(group) : { id: newId(), name: lt(''), minChoices: 0, maxChoices: 1, options: [] };
    const saved = await openDialog(close => {
      const body = h('div', { class: 'extras-edit' });
      const problem = h('p', { class: 'form-problem', role: 'alert' });
      const render = () => {
        const name = h('input', { class: 'text-input', type: 'text', maxlength: 60, value: text(draft.name), placeholder: 'e.g. Sauce, Extra toppings' });
        name.addEventListener('input', () => { draft.name = lt(name.value); });
        const stepper = (label, key, min) => h('div', { class: 'stepper' }, h('span', { class: 'field-label', text: label }),
          h('div', { class: 'qty' },
            h('button', { class: 'qty-btn', 'aria-label': `${label} fewer`, disabled: draft[key] <= min, onclick: () => { draft[key]--; if (draft.maxChoices < Math.max(1, draft.minChoices)) draft.maxChoices = Math.max(1, draft.minChoices); render(); }, text: '−' }),
            h('span', { class: 'qty-value', text: String(draft[key]) }),
            h('button', { class: 'qty-btn', 'aria-label': `${label} more`, onclick: () => { draft[key]++; if (draft.minChoices > draft.maxChoices) draft.maxChoices = draft.minChoices; render(); }, text: '+' })));
        mount(body,
          field('Name', name),
          h('div', { class: 'stepper-row' }, stepper('Must choose at least', 'minChoices', 0), stepper('Can choose up to', 'maxChoices', 1)),
          h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'Choices and prices' }),
            draft.options.map((o, i) => {
              const optName = h('input', { class: 'text-input size-name', type: 'text', maxlength: 60, value: text(o.name), 'aria-label': `Choice ${i + 1} name` });
              const price = h('button', {
                class: 'price-btn', 'aria-label': `Price of ${text(o.name) || 'choice'}`,
                onclick: async () => { const p = await amountDialog('Extra price', { initial: o.price.minorUnits, ok: 'Set price', allowZero: true }); if (p !== null) { o.price = money(p); render(); } },
              }, o.price.minorUnits ? `+${penceText(o.price.minorUnits)}` : 'Free');
              optName.addEventListener('input', () => { o.name = lt(optName.value); price.setAttribute('aria-label', `Price of ${optName.value || 'choice'}`); });
              return h('div', { class: 'size-row' }, optName, price,
                toggle('Sold out', o.isSoldOut, v => { o.isSoldOut = v; render(); }),
                h('button', { class: 'icon-btn', 'aria-label': `Remove ${text(o.name)}`, onclick: () => { draft.options.splice(i, 1); render(); }, text: '✕' }));
            }),
            h('button', { class: 'btn btn-small', onclick: () => { draft.options.push({ id: newId(), name: lt(''), price: money(0), isSoldOut: false }); render(); body.querySelector('.size-row:last-of-type input')?.focus(); }, text: '+ Add choice' })),
          problem);
      };
      render();
      return {
        title: isNew ? 'New extras' : `Extras: ${text(group.name)}`,
        wide: true,
        body,
        actions: [
          h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
          h('button', {
            class: 'btn btn-primary', text: 'Save extras',
            onclick: async () => {
              draft.name = lt(text(draft.name).trim());
              draft.options = draft.options.filter(o => text(o.name).trim());
              if (!text(draft.name)) { problem.textContent = 'Enter a name.'; return; }
              if (!draft.options.length) { problem.textContent = 'Add at least one choice.'; return; }
              try {
                await busy(() => api.put(`/api/menu/modifier-groups/${draft.id}`, { group: draft, original }));
                close(draft);
              } catch (e) {
                problem.textContent = e.message;
              }
            },
          }),
        ],
      };
    });

    if (!saved) return;
    toast(`${text(saved.name)} saved. Every item using it is updated.`, 'ok');
    if (isNew) onCreated?.(saved);
    const keep = this.panel;
    await this.till.loadMenu();
    this.panel = keep;
    this.render();
  }

  // ---------- Undo and change log ----------

  async undo() {
    try {
      const change = await busy(() => api.post('/api/menu/undo'));
      toast(change.summary, 'ok');
      this.panel = null;
      await this.reload();
    } catch (e) {
      toast(e.message, 'error');
    }
  }

  async showChanges() {
    const changes = await busy(() => api.get('/api/menu/changes?since=0'));
    await openDialog(close => ({
      title: 'Menu changes',
      wide: true,
      body: changes.length ? h('ol', { class: 'change-log' }, changes.slice(-50).reverse().map(c =>
        h('li', {}, h('span', { class: 'change-time', text: `${new Date(c.at).toLocaleDateString('en-GB')} ${timeText(c.at)}` }), h('span', { text: c.summary }))))
        : h('p', { class: 'empty-note', text: 'No changes yet.' }),
      actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Close' })],
    }));
  }
}

/** Quick sold-out switch for anyone taking orders (no other menu changes). */
export async function soldOutDialog(till) {
  let categoryId = till.categoryId;
  await openDialog(close => {
    const body = h('div', { class: 'soldout' });
    const render = () => {
      const view = till.menu;
      mount(body,
        h('div', { class: 'chips' }, view.categories().map(c => h('button', {
          class: `chip ${c.id === categoryId ? 'chip-on' : ''}`, onclick: () => { categoryId = c.id; render(); }, text: text(c.name),
        }))),
        h('div', { class: 'opt-grid' }, view.productsIn(categoryId).map(p => {
          const out = p.isSoldOut;
          return h('button', {
            class: `opt ${out ? 'opt-out' : ''}`, 'aria-pressed': String(out),
            onclick: async () => {
              try {
                await api.post(`/api/menu/products/${p.id}/sold-out`, { soldOut: !out });
                till.menu = new MenuView(await api.get('/api/menu'));
                render();
              } catch (e) { toast(e.message, 'error'); }
            },
          }, h('span', { class: 'opt-name', text: view.buttonName(p) }), h('span', { class: 'opt-price', text: out ? 'SOLD OUT — tap to bring back' : 'In stock' }));
        })));
    };
    render();
    return { title: 'Sold out items', wide: true, body, actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })] };
  });
  till.renderCatalog();
}
