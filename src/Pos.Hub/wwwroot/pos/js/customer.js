// Customer details for phone and delivery orders: find a past customer by phone number,
// or type name and address; check the postcode to get the distance from the shop.
import { api, ApiError } from './api.js';
import { h, mount, newId, timeText } from './dom.js';
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

    // "Find address": the service chosen in More → Address lookup (Google, Royal Mail online or the shop's files).
    const session = newId(); // groups Google's searches for one customer into one billing session
    const finder = h('input', { class: 'text-input', type: 'search', placeholder: 'Postcode or part of the address', 'aria-label': 'Find address', autocomplete: 'off' });
    const found = h('div', { class: 'pick', 'aria-live': 'polite' });
    let findSeq = 0;
    let findTimer;

    const check = async () => {
      problem.textContent = '';
      checked.className = 'check-result';
      checked.textContent = 'Checking…';
      try {
        const result = await api.get(`/api/delivery/check?postcode=${encodeURIComponent(postcode.el.value)}`);
        postcode.el.value = result.postcode;
        if (!town.el.value.trim() && result.town) town.el.value = result.town;
        checkedPostcode = result.postcode;
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
      } catch (e) {
        distance = null;
        checked.className = 'check-result check-bad';
        checked.textContent = e instanceof ApiError ? e.message : 'Could not check the postcode.';
        if (e instanceof ApiError && e.code === 'LookupUnavailable') { manual.open = true; miles.el.focus(); }
      }
    };

    const choose = async suggestion => {
      if (suggestion.isGroup) { await find(finder.value, suggestion.id); return; } // a postcode or street: list its addresses
      let address = suggestion.address;
      let resolvedMiles = null;
      if (!address) {
        try {
          const resolved = await api.get(`/api/addresses/resolve?id=${encodeURIComponent(suggestion.id)}&session=${session}`);
          address = resolved.address;
          resolvedMiles = resolved.distanceMiles;
        } catch (e) {
          mount(found, h('p', { class: 'check-result check-bad', text: `${e.message} Type the address in the boxes below.` }));
          return;
        }
      }

      line1.el.value = address.line1;
      line2.el.value = address.line2 ?? '';
      town.el.value = address.town ?? '';
      postcode.el.value = address.postcode;
      mount(found, h('button', { class: 'pick-chosen', onclick: () => { finder.focus(); find(finder.value); }, text: `✓ ${suggestion.label} — change` }));
      if (resolvedMiles !== null && resolvedMiles !== undefined) {
        distance = resolvedMiles;
        checkedPostcode = address.postcode;
        miles.el.value = String(distance);
        showDistance();
      } else {
        await check();
      }
    };

    const find = async (text, group, pickSingle = false) => {
      const seq = ++findSeq;
      if (!group && text.trim().length < 3) { mount(found); return; }
      mount(found, h('p', { class: 'field-hint', text: 'Searching…' }));
      try {
        const result = await api.get(`/api/addresses/search?q=${encodeURIComponent(text)}&session=${session}${group ? `&group=${encodeURIComponent(group)}` : ''}`);
        if (seq !== findSeq) return;
        mount(found,
          result.problem ? h('p', { class: 'check-result check-bad', text: result.problem }) : null,
          h('div', { class: 'pick-list', role: 'list' }, result.suggestions.map(sg => h('button', {
            class: `pick-item ${sg.isGroup ? 'pick-group' : ''}`, role: 'listitem', onclick: () => choose(sg), text: sg.isGroup ? `${sg.label} ›` : sg.label,
          }))),
          result.suggestions.length ? null : h('p', { class: 'field-hint', text: 'Nothing found. Type the address in the boxes below.' }),
          result.attribution ? h('p', { class: 'attribution', text: result.attribution }) : null);
        const only = result.suggestions.length === 1 ? result.suggestions[0] : null;
        if (pickSingle && only && !only.isGroup) await choose(only);
      } catch (e) {
        if (seq === findSeq) mount(found, h('p', { class: 'check-result check-bad', text: `${e.message} Type the address in the boxes below.` }));
      }
    };

    finder.addEventListener('input', () => { clearTimeout(findTimer); findTimer = setTimeout(() => find(finder.value), 350); });
    // Enter searches now (not with the list from before) and picks the address if only one matches.
    finder.addEventListener('keydown', e => {
      if (e.key !== 'Enter') return;
      clearTimeout(findTimer);
      find(finder.value, null, true);
    });

    // Typing a different postcode cancels the old distance.
    postcode.el.addEventListener('input', () => {
      if (checkedPostcode !== null && postcode.el.value.replace(/\s/g, '').toUpperCase() !== checkedPostcode.replace(/\s/g, '')) {
        distance = null;
        checkedPostcode = null;
        miles.el.value = '';
        checked.textContent = '';
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
          h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Find address' }), finder),
          found,
          line1.field, line2.field, town.field,
          h('div', { class: 'postcode-row' }, postcode.field, h('button', { class: 'btn btn-kitchen', onclick: check, text: 'Check postcode' })),
          checked,
          manual) : null,
        problem),
      actions: [
        h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: save, text: 'Use these details' }),
      ],
    };
  });
}
