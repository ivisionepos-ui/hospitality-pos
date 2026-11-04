// "Find address": one box used by the delivery customer panel and Shop details. It asks the service chosen in
// More → Address lookup (Google, Royal Mail online, or the shop's Royal Mail files) and lists what it finds.
import { api } from './api.js';
import { h, mount, newId } from './dom.js';

/**
 * Creates the box and its list.
 * @param {(address: object, miles: number|null, label: string) => Promise<void>|void} onChoose
 *   called with the full address, the distance from the shop when the service gave one, and the label tapped.
 * @returns {{ field: HTMLElement, list: HTMLElement, input: HTMLInputElement }}
 */
export function addressFinder(onChoose) {
  const session = newId(); // groups Google's searches for one address into one billing session
  const input = h('input', { class: 'text-input', type: 'search', placeholder: 'Postcode or part of the address', 'aria-label': 'Find address', autocomplete: 'off' });
  const list = h('div', { class: 'pick', 'aria-live': 'polite' });
  let seq = 0;
  let timer;

  const say = (text, bad = false) => mount(list, h('p', { class: bad ? 'check-result check-bad' : 'field-hint', text }));

  const choose = async suggestion => {
    if (suggestion.isGroup) { await find(input.value, suggestion.id); return; } // a postcode or street: list its addresses
    let address = suggestion.address;
    let miles = null;
    if (!address) {
      try {
        const resolved = await api.get(`/api/addresses/resolve?id=${encodeURIComponent(suggestion.id)}&session=${session}`);
        address = resolved.address;
        miles = resolved.distanceMiles ?? null;
      } catch (e) {
        say(`${e.message} Type the address in the boxes below.`, true);
        return;
      }
    }

    mount(list, h('button', { class: 'pick-chosen', onclick: () => { input.focus(); find(input.value); }, text: `✓ ${suggestion.label} — change` }));
    await onChoose(address, miles, suggestion.label);
  };

  const find = async (text, group, pickSingle = false) => {
    const mine = ++seq;
    if (!group && text.trim().length < 3) { mount(list); return; }
    say('Searching…');
    try {
      const result = await api.get(`/api/addresses/search?q=${encodeURIComponent(text)}&session=${session}${group ? `&group=${encodeURIComponent(group)}` : ''}`);
      if (mine !== seq) return;
      mount(list,
        result.problem ? h('p', { class: 'check-result check-bad', text: result.problem }) : null,
        h('div', { class: 'pick-list', role: 'list' }, result.suggestions.map(sg => h('button', {
          class: `pick-item ${sg.isGroup ? 'pick-group' : ''}`, role: 'listitem', onclick: () => choose(sg), text: sg.isGroup ? `${sg.label} ›` : sg.label,
        }))),
        result.suggestions.length ? null : h('p', { class: 'field-hint', text: emptyHint(result.provider) }),
        result.attribution ? h('p', { class: 'attribution', text: result.attribution }) : null);
      const only = result.suggestions.length === 1 ? result.suggestions[0] : null;
      if (pickSingle && only && !only.isGroup) await choose(only);
    } catch (e) {
      if (mine === seq) say(`${e.message} Type the address in the boxes below.`, true);
    }
  };

  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => find(input.value), 350); });
  // Enter searches now (not with the list from before) and picks the address if only one matches.
  input.addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    clearTimeout(timer);
    find(input.value, null, true);
  });

  return {
    input,
    list,
    field: h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Find address' }), input),
  };
}

/** Why nothing was found, in plain words. */
function emptyHint(provider) {
  return provider === 'LocalFile'
    ? 'Not in the shop’s address files (they cover only the areas loaded in More → Address files). Type the address in the boxes below.'
    : 'Nothing found. Type the address in the boxes below.';
}
