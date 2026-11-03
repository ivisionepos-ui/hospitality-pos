// Choosing an item's size and extras, and building a meal deal from its choices.
import { h, mount, penceText, text } from './dom.js';
import { openDialog } from './dialogs.js';

function rule(group) {
  if (group.minChoices === group.maxChoices) return `Choose ${group.minChoices}`;
  if (group.minChoices === 0) return `Optional, up to ${group.maxChoices}`;
  return `Choose ${group.minChoices}–${group.maxChoices}`;
}

/**
 * Size, extras, quantity and note for one item.
 * options.fixedVariantId: size already decided (meal deal choice). options.inDeal: no quantity, note or prices of sizes.
 * Resolves { variantId, extras, quantity, note, summary } or null.
 */
export function itemOptionsDialog(menu, product, options = {}) {
  const { fixedVariantId = null, inDeal = false, initial = null } = options;
  return openDialog(close => {
    const variants = product.variants.filter(v => !v.isSoldOut || v.id === fixedVariantId);
    const state = {
      variantId: fixedVariantId ?? initial?.variantId ?? (variants.length === 1 ? variants[0].id : null),
      extras: new Set(initial?.extras ?? []),
      quantity: initial?.quantity ?? 1,
      note: initial?.note ?? '',
    };
    const groups = menu.modifierGroups(product);
    const body = h('div', { class: 'options' });
    const addButton = h('button', { class: 'btn btn-primary btn-big' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });

    const extrasPence = () => groups.flatMap(g => g.options).filter(o => state.extras.has(o.id)).reduce((s, o) => s + o.price.minorUnits, 0);
    const variantPence = () => (state.variantId && !inDeal ? menu.variant(product, state.variantId).price.minorUnits : 0);
    const missing = () => {
      if (!state.variantId) return 'Choose a size.';
      for (const g of groups) {
        const n = g.options.filter(o => state.extras.has(o.id)).length;
        if (n < g.minChoices) return `${text(g.name)}: choose at least ${g.minChoices}.`;
      }
      return null;
    };

    const render = () => {
      const sections = [];
      if (variants.length > 1 && !fixedVariantId) {
        sections.push(h('section', { class: 'opt-section' },
          h('h3', { text: 'Size' }),
          h('div', { class: 'opt-grid' }, variants.map(v => h('button', {
            class: `opt ${state.variantId === v.id ? 'opt-on' : ''}`,
            'aria-pressed': String(state.variantId === v.id),
            onclick: () => { state.variantId = v.id; render(); },
          }, h('span', { class: 'opt-name', text: text(v.name) }), inDeal ? null : h('span', { class: 'opt-price', text: penceText(v.price.minorUnits) }))))));
      }

      for (const g of groups) {
        const chosen = g.options.filter(o => state.extras.has(o.id)).length;
        sections.push(h('section', { class: 'opt-section' },
          h('h3', {}, text(g.name), h('span', { class: `opt-rule ${chosen < g.minChoices ? 'opt-rule-needed' : ''}`, text: rule(g) })),
          h('div', { class: 'opt-grid' }, g.options.map(o => {
            const on = state.extras.has(o.id);
            const full = !on && chosen >= g.maxChoices && g.maxChoices > 1;
            return h('button', {
              class: `opt ${on ? 'opt-on' : ''}`,
              'aria-pressed': String(on),
              disabled: o.isSoldOut || full,
              onclick: () => {
                if (on) state.extras.delete(o.id);
                else {
                  if (g.maxChoices === 1) for (const other of g.options) state.extras.delete(other.id); // swap the single choice
                  state.extras.add(o.id);
                }
                render();
              },
            }, h('span', { class: 'opt-name', text: text(o.name) }),
              o.isSoldOut ? h('span', { class: 'opt-price', text: 'Sold out' }) : o.price.minorUnits ? h('span', { class: 'opt-price', text: `+${penceText(o.price.minorUnits)}` }) : null);
          }))));
      }

      if (!inDeal) {
        const note = h('input', { class: 'text-input', type: 'text', value: state.note, maxlength: 200, placeholder: 'e.g. well done, no onions', 'aria-label': 'Note for the kitchen' });
        note.addEventListener('input', () => { state.note = note.value; });
        sections.push(h('section', { class: 'opt-section opt-row' },
          h('div', { class: 'qty' },
            h('button', { class: 'qty-btn', 'aria-label': 'One fewer', disabled: state.quantity <= 1, onclick: () => { state.quantity--; render(); }, text: '−' }),
            h('span', { class: 'qty-value', text: String(state.quantity) }),
            h('button', { class: 'qty-btn', 'aria-label': 'One more', disabled: state.quantity >= 99, onclick: () => { state.quantity++; render(); }, text: '+' })),
          h('label', { class: 'note-field' }, h('span', { class: 'field-label', text: 'Note for the kitchen' }), note)));
      }

      mount(body, sections);
      const each = variantPence() + extrasPence();
      addButton.textContent = inDeal ? 'Choose' : `Add  ${penceText(each * state.quantity)}`;
      problem.textContent = '';
    };

    addButton.addEventListener('click', () => {
      const why = missing();
      if (why) { problem.textContent = why; return; }
      const extrasNames = groups.flatMap(g => g.options).filter(o => state.extras.has(o.id)).map(o => text(o.name));
      const sizeName = product.variants.length > 1 ? text(menu.variant(product, state.variantId).name) : null;
      close({
        variantId: state.variantId,
        extras: [...state.extras],
        quantity: state.quantity,
        note: state.note.trim() || null,
        summary: { size: sizeName, extras: extrasNames },
        unitPence: variantPence() + extrasPence(),
      });
    });

    render();
    return {
      title: text(product.name),
      wide: groups.length > 0,
      body: [body, problem],
      actions: [h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }), addButton],
    };
  });
}

/**
 * Builds a meal deal: one or more choices per slot.
 * Resolves { variantId, choices: [{ slotId, productId, variantId, extras, label }], quantity, note, unitPence } or null.
 */
export function dealDialog(menu, deal) {
  return openDialog(close => {
    const variantId = deal.variants.find(v => !v.isSoldOut)?.id ?? deal.variants[0].id;
    const basePence = menu.variant(deal, variantId).price.minorUnits;
    const picked = new Map(deal.dealSlots.map(s => [s.id, []]));
    const body = h('div', { class: 'deal' });
    const addButton = h('button', { class: 'btn btn-primary btn-big' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    let quantity = 1;

    const totalPence = () => basePence + [...picked.values()].flat().reduce((s, c) => s + c.upcharge + c.extrasPence, 0);

    const choose = async (slot, choice) => {
      const product = choice.product;
      const fixed = choice.variantId;
      const needsSize = !fixed && product.variants.filter(v => !v.isSoldOut).length > 1;
      let result;
      if (needsSize || menu.modifierGroups(product).length > 0) {
        result = await itemOptionsDialog(menu, product, { fixedVariantId: fixed, inDeal: true });
        if (!result) return;
      } else {
        const v = fixed ?? product.variants.find(x => !x.isSoldOut)?.id ?? product.variants[0].id;
        result = { variantId: v, extras: [], summary: { size: null, extras: [] }, unitPence: 0 };
      }

      const extrasPence = menu.modifierGroups(product).flatMap(g => g.options).filter(o => result.extras.includes(o.id)).reduce((s, o) => s + o.price.minorUnits, 0);
      const size = product.variants.length > 1 ? text(menu.variant(product, result.variantId).name) : null;
      picked.get(slot.id).push({
        slotId: slot.id, productId: product.id, variantId: result.variantId, extras: result.extras,
        upcharge: choice.upcharge, extrasPence,
        label: [size, text(product.name)].filter(Boolean).join(' ') + (result.summary.extras.length ? ` (${result.summary.extras.join(', ')})` : ''),
      });
      render();
    };

    const render = () => {
      mount(body, deal.dealSlots.map((slot, index) => {
        const chosen = picked.get(slot.id);
        const full = chosen.length >= slot.maxChoices;
        const short = chosen.length < slot.minChoices;
        return h('section', { class: `deal-slot ${short ? 'deal-slot-open' : 'deal-slot-done'}` },
          h('h3', {},
            h('span', { class: 'deal-step', text: String(index + 1) }),
            text(slot.name),
            h('span', { class: `opt-rule ${short ? 'opt-rule-needed' : ''}`, text: rule(slot) })),
          chosen.length ? h('div', { class: 'chips' }, chosen.map((c, i) => h('button', {
            class: 'chip chip-on', 'aria-label': `Remove ${c.label}`,
            onclick: () => { chosen.splice(i, 1); render(); },
          }, c.label, c.upcharge ? ` +${penceText(c.upcharge)}` : '', ' ✕'))) : null,
          full ? null : h('div', { class: 'opt-grid' }, menu.slotChoices(slot).map(choice => {
            const soldOut = menu.isSoldOut(choice.product) || (choice.variantId && menu.variant(choice.product, choice.variantId)?.isSoldOut);
            const size = choice.variantId && choice.product.variants.length > 1 ? text(menu.variant(choice.product, choice.variantId).name) + ' ' : '';
            return h('button', { class: 'opt', disabled: soldOut, onclick: () => choose(slot, choice) },
              h('span', { class: 'opt-name', text: size + menu.buttonName(choice.product) }),
              soldOut ? h('span', { class: 'opt-price', text: 'Sold out' }) : choice.upcharge ? h('span', { class: 'opt-price', text: `+${penceText(choice.upcharge)}` }) : null);
          })));
      }),
      h('section', { class: 'opt-section opt-row' },
        h('div', { class: 'qty' },
          h('button', { class: 'qty-btn', 'aria-label': 'One fewer', disabled: quantity <= 1, onclick: () => { quantity--; render(); }, text: '−' }),
          h('span', { class: 'qty-value', text: String(quantity) }),
          h('button', { class: 'qty-btn', 'aria-label': 'One more', disabled: quantity >= 20, onclick: () => { quantity++; render(); }, text: '+' }))));
      addButton.textContent = `Add deal  ${penceText(totalPence() * quantity)}`;
      problem.textContent = '';
    };

    addButton.addEventListener('click', () => {
      const open = deal.dealSlots.find(s => picked.get(s.id).length < s.minChoices);
      if (open) { problem.textContent = `${text(open.name)}: not chosen yet.`; return; }
      const choices = [...picked.values()].flat();
      close({ variantId, choices, quantity, note: null, unitPence: totalPence() });
    });

    render();
    return {
      title: `${text(deal.name)} — ${penceText(basePence)}`,
      wide: true,
      body: [body, problem],
      actions: [h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }), addButton],
    };
  });
}
