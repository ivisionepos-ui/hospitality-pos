// Staff: the staff list (add, rename, change role, reset PIN, remove, bring back), the record of changes,
// "Change my PIN" for everyone, and the list of registered tills and screens (owner).
import { api, ApiError, credentials } from './api.js';
import { h, mount, timeText, toast } from './dom.js';
import { busy, confirmDialog, keypad, openDialog } from './dialogs.js';

export const ROLES = [
  ['Cashier', 'Counter, phone and online orders'],
  ['Waiter', 'Takes table orders'],
  ['Kitchen', 'Kitchen screen only'],
  ['Driver', 'Takes deliveries; sees their own on the till'],
  ['Manager', 'Approvals, menu, reports, cash, staff'],
  ['Owner', 'Everything, including tills and shop settings'],
];

/** Roles this person may give: owners any, managers only those below manager (the hub enforces the same). */
function rolesFor(myRole) {
  return myRole === 'Owner' ? ROLES : ROLES.filter(([r]) => r !== 'Manager' && r !== 'Owner');
}

function problemText(e) {
  return e instanceof ApiError ? e.message : 'Could not save. Check the hub is running.';
}

/**
 * Asks for a PIN on the keypad. steps: list of prompts ("Enter the new PIN", "Enter it again").
 * Each step must match the first (for "again"). Resolves the PIN, or null if cancelled.
 */
export function pinDialog(title, steps, { check = null } = {}) {
  return openDialog(close => {
    const prompt = h('p', { class: 'dialog-message' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    let step = 0;
    let first = null;
    const show = () => { prompt.textContent = steps[step]; };
    const pad = keypad({
      mode: 'pin', maxDigits: 8, enterText: 'OK',
      onChange: () => { problem.textContent = ''; },
      onEnter: pin => {
        // Clearing the keypad clears the message too, so the message is set after.
        if (!/^\d{4,8}$/.test(pin)) { problem.textContent = 'A PIN is 4 to 8 digits.'; return; }
        const issue = step === 0 && check ? check(pin) : null;
        if (issue) { pad.clear(); problem.textContent = issue; return; }
        if (step === 0) first = pin;
        else if (pin !== first) { step = 0; first = null; pad.clear(); show(); problem.textContent = 'The two PINs are different. Start again.'; return; }
        if (step === steps.length - 1) { close(first ?? pin); return; }
        step += 1;
        pad.clear();
        show();
      },
    });
    show();
    return { title, class: 'dialog-narrow', body: [prompt, pad.el, problem] };
  });
}

/** The hub refuses these too; checking here saves a round trip and explains straight away. */
function easyPin(pin) {
  if (new Set(pin).size === 1 || '0123456789'.includes(pin) || '9876543210'.includes(pin)) {
    return 'That PIN is too easy to guess (like 1111 or 1234). Choose another.';
  }
  return null;
}

const NEW_PIN = ['Enter the new PIN (4 to 8 digits).', 'Enter the same PIN again.'];

/** Name and role form for adding or editing. Resolves { name, role } or null. */
function staffForm(title, { name = '', role = 'Cashier', myRole, isSelf = false, save = 'Save' }) {
  return openDialog(close => {
    const input = h('input', { class: 'text-input', type: 'text', maxlength: 40, value: name, autocomplete: 'off', 'aria-label': 'Name' });
    const problem = h('p', { class: 'form-problem', role: 'alert' });
    let chosen = role;
    const roles = h('div', { class: 'role-list', role: 'radiogroup', 'aria-label': 'Role' });
    const allowed = rolesFor(myRole);
    const renderRoles = () => mount(roles, (isSelf ? ROLES.filter(([r]) => r === role) : allowed).map(([id, text]) => h('button', {
      type: 'button', class: `role-btn ${chosen === id ? 'role-on' : ''}`, role: 'radio', 'aria-checked': String(chosen === id), 'aria-label': id,
      onclick: () => { chosen = id; renderRoles(); },
    }, h('strong', { text: id }), h('span', { text }))));
    renderRoles();
    const submit = () => {
      const clean = input.value.trim().replace(/\s+/g, ' ');
      if (!clean) { problem.textContent = 'Enter a name.'; input.focus(); return; }
      close({ name: clean, role: chosen });
    };
    input.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
    return {
      title,
      focus: input,
      body: [
        h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Name (as it shows on the till and reports)' }), input),
        h('div', { class: 'field' }, h('span', { class: 'field-label', text: isSelf ? 'Role (nobody can change their own role)' : 'Role' }), roles),
        problem,
      ],
      actions: [
        h('button', { class: 'btn', onclick: () => close(null), text: 'Cancel' }),
        h('button', { class: 'btn btn-primary', onclick: submit, text: save }),
      ],
    };
  });
}

/** Keeps asking until the hub accepts (or the person cancels), showing the hub's explanation each time. Resolves null if not saved. */
async function untilSaved(ask, save) {
  let value = null;
  for (;;) {
    value = await ask(value);
    if (value === null) return null;
    try {
      return (await busy(() => save(value))) ?? true; // "saved" even when the hub answers with no content
    } catch (e) {
      toast(problemText(e), 'error', 6000);
      if (!(e instanceof ApiError) || e.status !== 400) return null; // only a fixable entry (bad name or PIN) is asked again
    }
  }
}

async function addStaff(me) {
  const details = await staffForm('Add staff', { myRole: me.role, save: 'Next: PIN' });
  if (!details) return false;
  const added = await untilSaved(
    () => pinDialog(`PIN for ${details.name}`, NEW_PIN, { check: easyPin }),
    pin => api.post('/api/staff', { ...details, pin }));
  if (added) toast(`${added.name} added as ${added.role}. They log in with their PIN.`, 'ok', 5000);
  return Boolean(added);
}

async function editStaff(me, person) {
  const isSelf = person.id === me.id;
  const changed = await untilSaved(
    previous => staffForm(`Edit ${person.name}`, { ...person, ...(previous ?? {}), myRole: me.role, isSelf }),
    details => api.put(`/api/staff/${person.id}`, details));
  if (!changed) return false;
  toast(changed.role !== person.role ? `${changed.name} is now ${changed.role}. They need to log in again.` : `Saved ${changed.name}.`, 'ok', 5000);
  return true;
}

async function resetPin(person) {
  const done = await untilSaved(
    () => pinDialog(`New PIN for ${person.name}`, NEW_PIN, { check: easyPin }),
    pin => api.post(`/api/staff/${person.id}/pin`, { pin }));
  if (done !== null) toast(`New PIN set for ${person.name}. Tell them in private.`, 'ok', 5000);
  return done !== null;
}

async function removeStaff(person) {
  if (!(await confirmDialog(`Remove ${person.name}?`, 'They can no longer log in, and are logged out of every till now. Their past orders and records are kept. You can bring them back later.', 'Remove', true))) return false;
  await busy(() => api.del(`/api/staff/${person.id}`));
  toast(`${person.name} removed.`, 'ok');
  return true;
}

async function bringBack(person) {
  const back = await untilSaved(
    () => pinDialog(`Bring back ${person.name}: new PIN`, NEW_PIN, { check: easyPin }),
    pin => api.post(`/api/staff/${person.id}/reactivate`, { pin }));
  if (back) toast(`${person.name} is back as ${person.role}.`, 'ok');
  return Boolean(back);
}

async function historyDialog(person) {
  const changes = await busy(() => api.get(person ? `/api/staff/history?staffId=${person.id}` : '/api/staff/history'));
  await openDialog(close => ({
    title: person ? `Changes to ${person.name}` : 'Staff changes',
    wide: true,
    body: changes.length
      ? h('table', { class: 'report staff-history' },
        h('thead', {}, h('tr', {}, ['When', 'Who', 'Change', 'By'].map(t => h('th', { scope: 'col', text: t })))),
        h('tbody', {}, changes.map(c => h('tr', {},
          h('td', { text: `${new Date(c.at).toLocaleDateString('en-GB')} ${timeText(c.at)}` }),
          h('td', { text: c.staffName }),
          h('td', { text: c.change }),
          h('td', { text: c.changedBy })))))
      : h('p', { class: 'empty-note', text: 'No changes yet.' }),
    actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })],
  }));
}

/** The staff list (managers and owners). */
export async function staffDialog(me) {
  let staff = await busy(() => api.get('/api/staff'));
  const roleOrder = Object.fromEntries(ROLES.map(([r], i) => [r, i]));
  await openDialog(close => {
    const body = h('div', { class: 'staff-list' });
    const canManage = p => p.id !== me.id && (me.role === 'Owner' || !['Manager', 'Owner'].includes(p.role));
    const act = action => async () => {
      try {
        if (await action()) {
          staff = await api.get('/api/staff');
          render();
        }
      } catch (e) {
        toast(problemText(e), 'error', 6000);
      }
    };
    const row = p => h('div', { class: `staff-row ${p.isActive ? '' : 'staff-removed'}`, 'aria-label': p.name },
      h('div', { class: 'staff-who' },
        h('strong', { text: p.name + (p.id === me.id ? ' (you)' : '') }),
        h('span', { class: `role-tag role-${p.role.toLowerCase()}`, text: p.isActive ? p.role : `${p.role} · removed` })),
      h('div', { class: 'staff-actions' },
        p.isActive && (canManage(p) || p.id === me.id) ? h('button', { class: 'btn btn-small', onclick: act(() => editStaff(me, p)), text: 'Edit' }) : null,
        p.isActive && canManage(p) ? h('button', { class: 'btn btn-small', onclick: act(() => resetPin(p)), text: 'New PIN' }) : null,
        p.isActive && canManage(p) ? h('button', { class: 'btn btn-small btn-danger-soft', onclick: act(() => removeStaff(p)), text: 'Remove' }) : null,
        !p.isActive && canManage(p) ? h('button', { class: 'btn btn-small', onclick: act(() => bringBack(p)), text: 'Bring back' }) : null,
        h('button', { class: 'btn btn-small btn-quiet', onclick: act(async () => { await historyDialog(p); return false; }), text: 'History' })));
    const render = () => {
      const active = staff.filter(p => p.isActive).sort((a, b) => roleOrder[b.role] - roleOrder[a.role] || a.name.localeCompare(b.name));
      const removed = staff.filter(p => !p.isActive);
      mount(body,
        active.map(row),
        removed.length ? h('h3', { class: 'menu-heading', text: 'Removed' }) : null,
        removed.map(row));
    };
    render();
    return {
      title: 'Staff',
      wide: true,
      body: [
        h('p', { class: 'field-hint', text: me.role === 'Owner' ? 'Each person logs in with their own PIN. Never share PINs.' : 'Managers can add and change cashiers, waiters and kitchen staff. Ask the owner for managers.' }),
        body,
      ],
      actions: [
        h('button', { class: 'btn', onclick: act(async () => { await historyDialog(null); return false; }), text: 'All changes' }),
        h('button', { class: 'btn btn-primary', onclick: act(() => addStaff(me)), text: '+ Add staff' }),
        h('button', { class: 'btn', onclick: () => close(), text: 'Done' }),
      ],
    };
  });
}

/** Anyone: change your own PIN. Needs the current PIN; afterwards you log in again with the new one. */
export async function changeMyPin(me, logout) {
  const current = await pinDialog('Change my PIN', ['Enter your current PIN.']);
  if (!current) return;
  const done = await untilSaved(
    () => pinDialog('Change my PIN', NEW_PIN, { check: pin => easyPin(pin) ?? (pin === current ? 'That is your current PIN. Choose a new one.' : null) }),
    pin => api.post(`/api/staff/${me.id}/pin`, { pin, currentPin: current }));
  if (done === null) return;
  toast('PIN changed. Log in with your new PIN.', 'ok', 6000);
  logout();
}

const DEVICE_TYPES = { Till: 'Till', KitchenScreen: 'Kitchen screen' };

/** Registered tills and screens (owner): see them, remove a lost or replaced one. */
export async function devicesDialog() {
  let devices = await busy(() => api.get('/api/devices'));
  const mine = credentials.device?.deviceId;
  await openDialog(close => {
    const body = h('div', { class: 'staff-list' });
    const render = () => mount(body, devices.filter(d => d.isActive).map(d => h('div', { class: 'staff-row', 'aria-label': d.name },
      h('div', { class: 'staff-who' },
        h('strong', { text: d.name + (d.id === mine ? ' (this one)' : '') }),
        h('span', { class: 'role-tag', text: DEVICE_TYPES[d.type] ?? d.type })),
      h('div', { class: 'staff-actions' },
        d.id === mine ? null : h('button', {
          class: 'btn btn-small btn-danger-soft', text: 'Remove',
          onclick: async () => {
            if (!(await confirmDialog(`Remove ${d.name}?`, 'It stops working straight away (use this for a lost, stolen or replaced till). To use it again, register it with a new code.', 'Remove', true))) return;
            try {
              await busy(() => api.del(`/api/devices/${d.id}`));
              devices = await api.get('/api/devices');
              toast(`${d.name} removed.`, 'ok');
              render();
            } catch (e) {
              toast(problemText(e), 'error', 6000);
            }
          },
        })))));
    render();
    return {
      title: 'Tills and screens',
      body: [h('p', { class: 'field-hint', text: 'Every till and kitchen screen registered with this hub. To add one: More → Add a till or screen.' }), body],
      actions: [h('button', { class: 'btn btn-primary', onclick: () => close(), text: 'Done' })],
    };
  });
}
