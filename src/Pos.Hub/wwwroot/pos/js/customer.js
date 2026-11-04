// Customer details for phone and delivery orders: find a past customer by phone number,
// or type name and address; check the postcode to get the distance from the shop.
import { api, ApiError } from './api.js';
import { h, mount, timeText } from './dom.js';
import { openDialog } from './dialogs.js';

function input(label, value, attrs = {}) {
  const el = h('input', { class: 'text-input', type: 'text', value: value ?? '', autocomplete: 'off', ...attrs });
  return { el, field: h('label', { class: 'field' }, h('span', { class: 'field-label', text: label }), el) };
}

/** "07700900123" → "07700 900123". */
export function phoneText(digits) {
  return digits && digits.length === 11 ? `${digits.slice(0, 5)} ${digits.slice(5)}` : digits ?? '';
}

/** One line about a customer, for the order screen. */
export function contactSummary(contact) {
  if (!contact) return '';
  const a = contact.address;
  return [contact.name, phoneText(contact.phone), a ? [a.line1, a.postcode].filter(Boolean).join(', ') : null].filter(Boolean).join(' · ');
}

/**
 * Opens the customer panel. Resolves { contact, distanceMiles } or null.
 * @param {{ delivery: boolean, initial?: { contact?: object, distanceMiles?: number } }} options
 */
export function customerDialog({ delivery, initial }) {
  const c = initial?.contact ?? {};
  const a = c.address ?? {};
  return openDialog(close => {
    const phone = input('Phone number', phoneText(c.phone), { type: 'tel', inputmode: 'tel', maxlength: 20, placeholder: 'Type to find a past customer' });
    const name = input('Name', c.name, { maxlength: 80 });
    const line1 = input('House number and street', a.line1, { maxlength: 120, placeholder: 'e.g. 12 Park Lane' });
    const line2 = input('Flat or building (optional)', a.line2, { maxlength: 120 });
    const town = input('Town (optional)', a.town, { maxlength: 60 });
    const postcode = input('Postcode', a.postcode, { maxlength: 9, placeholder: 'e.g. LS2 9JT', class: 'text-input postcode-input' });
    const miles = input('Distance in miles', initial?.distanceMiles ?? '', { inputmode: 'decimal', maxlength: 5 });
    const notes = input(delivery ? 'Notes for the driver (optional)' : 'Notes (optional)', c.notes, { maxlength: 200, placeholder: delivery ? 'e.g. Ring the side door' : '' });
    const manual = h('details', { class: 'manual-miles' }, h('summary', { text: 'No internet? Type the miles' }), miles.field);
    const matches = h('div', { class: 'matches', 'aria-live': 'polite' });
    const checked = h('p', { class: 'check-result', role: 'status' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    let distance = initial?.distanceMiles ?? null;
    let checkedPostcode = distance !== null ? (a.postcode ?? '') : null;

    const showDistance = () => {
      checked.className = 'check-result check-ok';
      checked.textContent = `${distance} miles from the shop`;
    };
    if (distance !== null) showDistance();

    // The shop's address files: every address in the postcode, narrowed by what is typed in the filter.
    const pick = h('div', { class: 'pick' });
    let houses = [];
    const choose = option => {
      line1.el.value = option.address.line1;
      line2.el.value = option.address.line2 ?? '';
      town.el.value = option.address.town ?? '';
      postcode.el.value = option.address.postcode;
      mount(pick, h('button', { class: 'pick-chosen', onclick: () => showPick(), text: `✓ ${option.label} — change` }));
    };
    const showPick = () => {
      if (!houses.length) { mount(pick); return; }
      const filter = h('input', { class: 'text-input', type: 'search', placeholder: 'Type house number, flat or name', 'aria-label': 'Find the address', autocomplete: 'off' });
      const list = h('div', { class: 'pick-list', role: 'list' });
      const render = () => {
        const words = filter.value.toLowerCase().split(/[\s,]+/).filter(Boolean);
        const shown = houses.filter(o => words.every(w => o.label.toLowerCase().split(/[\s,]+/).some(part => part.startsWith(w))));
        mount(list, shown.slice(0, 60).map(o => h('button', { class: 'pick-item', role: 'listitem', onclick: () => choose(o), text: o.label })),
          shown.length > 60 ? h('p', { class: 'field-hint', text: `${shown.length - 60} more: type to narrow the list.` }) : null,
          shown.length ? null : h('p', { class: 'field-hint', text: 'No address matches. Type it in the boxes below.' }));
      };
      filter.addEventListener('input', render);
      filter.addEventListener('keydown', e => { if (e.key === 'Enter') list.querySelector('.pick-item')?.click(); });
      mount(pick, h('span', { class: 'field-label', text: `Choose the address (${houses.length} in ${postcode.el.value})` }), filter, list);
      render();
      filter.focus();
    };

    const check = async () => {
      problem.textContent = '';
      checked.className = 'check-result';
      checked.textContent = 'Checking…';
      try {
        const result = await api.get(`/api/delivery/check?postcode=${encodeURIComponent(postcode.el.value)}`);
        postcode.el.value = result.postcode;
        if (!town.el.value.trim() && result.town) town.el.value = result.town;
        checkedPostcode = result.postcode;
        houses = result.addresses ?? [];
        if (result.distanceMiles === null || result.distanceMiles === undefined) {
          distance = null;
          checked.className = 'check-result check-bad';
          checked.textContent = result.problem ?? 'The distance is not known: type the miles.';
          manual.open = true;
        } else {
          distance = result.distanceMiles;
          miles.el.value = String(distance);
          checked.textContent = `${distance} miles from the shop${result.town ? ` · ${result.town}` : ''}`;
          checked.className = 'check-result check-ok';
        }

        // Already chosen (e.g. a returning customer)? Keep it; otherwise offer the list.
        const current = houses.find(o => o.address.line1 === line1.el.value.trim() && (o.address.line2 ?? '') === line2.el.value.trim());
        if (current) choose(current);
        else showPick();
      } catch (e) {
        distance = null;
        houses = [];
        mount(pick);
        checked.className = 'check-result check-bad';
        checked.textContent = e instanceof ApiError ? e.message : 'Could not check the postcode.';
        if (e instanceof ApiError && e.code === 'LookupUnavailable') { manual.open = true; miles.el.focus(); }
      }
    };

    // Typing a different postcode cancels the old distance.
    postcode.el.addEventListener('input', () => {
      if (checkedPostcode !== null && postcode.el.value.replace(/\s/g, '').toUpperCase() !== checkedPostcode.replace(/\s/g, '')) {
        distance = null;
        checkedPostcode = null;
        miles.el.value = '';
        checked.textContent = '';
        houses = [];
        mount(pick);
      }
    });
    postcode.el.addEventListener('keydown', e => { if (e.key === 'Enter') check(); });
    miles.el.addEventListener('input', () => {
      const v = Number(miles.el.value.replace(',', '.'));
      distance = miles.el.value.trim() && Number.isFinite(v) && v >= 0 && v <= 100 ? Math.round(v * 10) / 10 : null;
      if (distance !== null) showDistance();
    });

    const fill = customer => {
      phone.el.value = phoneText(customer.phone);
      name.el.value = customer.name ?? '';
      notes.el.value = customer.notes ?? '';
      if (customer.address) {
        line1.el.value = customer.address.line1 ?? '';
        line2.el.value = customer.address.line2 ?? '';
        town.el.value = customer.address.town ?? '';
        postcode.el.value = customer.address.postcode ?? '';
        if (delivery) check();
      }
      mount(matches);
    };

    let searchTimer;
    let searchSeq = 0;
    phone.el.addEventListener('input', () => {
      clearTimeout(searchTimer);
      const digits = phone.el.value.replace(/\D/g, '');
      if (digits.length < 4) { mount(matches); return; }
      const seq = ++searchSeq;
      searchTimer = setTimeout(async () => {
        try {
          const found = await api.get(`/api/customers?phone=${encodeURIComponent(digits)}`);
          if (seq !== searchSeq) return;
          mount(matches, found.map(cu => h('button', { class: 'match', onclick: () => fill(cu) },
            h('strong', { text: cu.name ?? 'No name' }), ` · ${phoneText(cu.phone)}`,
            cu.address ? h('span', { class: 'match-address', text: [cu.address.line1, cu.address.postcode].join(', ') }) : null,
            h('span', { class: 'match-count', text: `${cu.orderCount} order${cu.orderCount === 1 ? '' : 's'}${cu.lastOrderAt ? `, last ${new Date(cu.lastOrderAt).toLocaleDateString('en-GB')} ${timeText(cu.lastOrderAt)}` : ''}` }))));
        } catch { /* search is a help only */ }
      }, 250);
    });

    const save = () => {
      problem.textContent = '';
      const digits = phone.el.value.replace(/[^\d+]/g, '');
      if (delivery && !digits) { problem.textContent = 'Enter a phone number, so the driver can call.'; return; }
      if (!delivery && !digits && !name.el.value.trim()) { problem.textContent = 'Enter a name or a phone number.'; return; }
      if (delivery && !line1.el.value.trim()) { problem.textContent = 'Enter the house number and street.'; return; }
      if (delivery && !postcode.el.value.trim()) { problem.textContent = 'Enter the postcode.'; return; }
      if (delivery && distance === null) { problem.textContent = 'Press Check postcode, or type the distance in miles.'; return; }
      close({
        contact: {
          name: name.el.value.trim() || null,
          phone: digits || null,
          address: delivery ? { line1: line1.el.value.trim(), line2: line2.el.value.trim() || null, town: town.el.value.trim() || null, postcode: postcode.el.value.trim() } : null,
          notes: notes.el.value.trim() || null,
        },
        distanceMiles: delivery ? distance : null,
      });
    };

    return {
      title: delivery ? 'Delivery customer' : 'Customer',
      wide: true,
      focus: phone.el,
      body: h('div', { class: 'customer-form' },
        h('div', { class: 'customer-col' }, phone.field, matches, name.field, notes.field),
        delivery ? h('div', { class: 'customer-col' },
          h('div', { class: 'postcode-row' }, postcode.field, h('button', { class: 'btn btn-kitchen', onclick: check, text: 'Check postcode' })),
          checked,
          pick,
          line1.field, line2.field, town.field,
          manual) : null,
        problem),
      actions: [
        h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: save, text: 'Use these details' }),
      ],
    };
  });
}
