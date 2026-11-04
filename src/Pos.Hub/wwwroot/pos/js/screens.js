// First-time set-up, registering a till, and staff PIN login.
import { api, ApiError, credentials } from './api.js';
import { h, mount } from './dom.js';
import { busy, keypad } from './dialogs.js';

function card(title, subtitle, ...content) {
  return h('div', { class: 'gate' },
    h('div', { class: 'gate-card' },
      h('h1', { class: 'gate-title', text: title }),
      subtitle ? h('p', { class: 'gate-sub', text: subtitle }) : null,
      content));
}

function field(label, input) {
  return h('label', { class: 'field' }, h('span', { class: 'field-label', text: label }), input);
}

/** First-time set-up on the hub PC: the owner and this PC's till. */
export function setupScreen(root, done) {
  const name = h('input', { class: 'text-input', type: 'text', maxlength: 60, autocomplete: 'name', required: true });
  const pin = h('input', { class: 'text-input', type: 'password', inputmode: 'numeric', maxlength: 8, autocomplete: 'new-password' });
  const pin2 = h('input', { class: 'text-input', type: 'password', inputmode: 'numeric', maxlength: 8, autocomplete: 'new-password' });
  const till = h('input', { class: 'text-input', type: 'text', maxlength: 40, value: 'Till 1' });
  const problem = h('p', { class: 'form-problem', role: 'alert' });
  const submit = async e => {
    e.preventDefault();
    if (!name.value.trim()) { problem.textContent = 'Enter your name.'; return; }
    if (!/^\d{4,8}$/.test(pin.value)) { problem.textContent = 'The PIN must be 4 to 8 digits.'; return; }
    if (pin.value !== pin2.value) { problem.textContent = 'The two PINs are different.'; return; }
    try {
      const result = await busy(() => api.post('/api/setup', { ownerName: name.value.trim(), pin: pin.value, deviceName: till.value.trim() || 'Till 1' }, { noSession: true }));
      credentials.device = { ...result.device, name: till.value.trim() || 'Till 1' };
      credentials.session = result.session;
      done();
    } catch (err) {
      problem.textContent = err instanceof ApiError ? err.message : 'Set-up failed.';
    }
  };
  mount(root, card('Welcome', 'First-time set-up of this shop. You will be the owner, with full access.',
    h('form', { class: 'form', onsubmit: submit },
      field('Your name', name),
      field('Your PIN (4–8 digits, keep it secret)', pin),
      field('PIN again', pin2),
      field('Name for this till', till),
      problem,
      h('button', { class: 'btn btn-primary btn-big', type: 'submit', text: 'Set up the shop' }))));
  name.focus();
}

/** Registering a till or screen with a pairing code from the owner. */
export function pairScreen(root, done, note) {
  const code = h('input', { class: 'text-input code-input', type: 'text', inputmode: 'numeric', maxlength: 6, autocomplete: 'one-time-code' });
  const name = h('input', { class: 'text-input', type: 'text', maxlength: 40, value: 'Till 2' });
  const problem = h('p', { class: 'form-problem', role: 'alert', text: note ?? '' });
  let type = 'Till';
  const kinds = h('div', { class: 'seg', role: 'radiogroup', 'aria-label': 'This device is a' });
  const renderKinds = () => mount(kinds, [['Till', 'Till'], ['KitchenScreen', 'Kitchen screen']].map(([id, label]) => h('button', {
    type: 'button', class: `seg-btn ${type === id ? 'seg-on' : ''}`, role: 'radio', 'aria-checked': String(type === id),
    onclick: () => {
      if (name.value === (type === 'Till' ? 'Till 2' : 'Kitchen 1')) name.value = id === 'Till' ? 'Till 2' : 'Kitchen 1';
      type = id;
      renderKinds();
    },
    text: label,
  })));
  renderKinds();
  const submit = async e => {
    e.preventDefault();
    if (!/^\d{6}$/.test(code.value.trim())) { problem.textContent = 'The code has 6 digits.'; return; }
    try {
      const device = await busy(() => api.post('/api/devices/pair', { code: code.value.trim(), name: name.value.trim() || 'Till', type }, { noSession: true }));
      credentials.device = { ...device, name: name.value.trim() || 'Till' };
      credentials.session = null;
      done();
    } catch (err) {
      problem.textContent = err instanceof ApiError ? err.message : 'Could not register.';
    }
  };
  mount(root, card('Register this device', 'On the owner’s till, open More → Add a till or screen to get a 6-digit code.',
    h('form', { class: 'form', onsubmit: submit },
      field('Code', code),
      h('div', { class: 'field' }, h('span', { class: 'field-label', text: 'This device is a' }), kinds),
      field('Name', name),
      problem,
      h('button', { class: 'btn btn-primary btn-big', type: 'submit', text: 'Register' }))));
  code.focus();
}

/** Staff PIN login. */
export function loginScreen(root, done) {
  const problem = h('p', { class: 'form-problem', role: 'alert' });
  const clock = h('div', { class: 'gate-clock' });
  const tick = () => { clock.textContent = new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }); };
  tick();
  const timer = setInterval(() => (document.body.contains(clock) ? tick() : clearInterval(timer)), 10000);
  const pad = keypad({
    mode: 'pin', maxDigits: 8, enterText: 'Log in',
    onChange: () => { problem.textContent = ''; },
    onEnter: async pin => {
      if (pin.length < 4) { problem.textContent = 'Enter your PIN.'; return; }
      try {
        credentials.session = await busy(() => api.post('/api/auth/login', { pin }, { noSession: true }));
        done();
      } catch (err) {
        pad.clear();
        problem.textContent = err instanceof ApiError ? err.message : 'Login failed.';
      }
    },
  });
  mount(root, card(credentials.device?.name ?? 'Till', 'Enter your PIN', clock, pad.el, problem,
    h('p', { class: 'version', text: `Version ${window.posVersion ?? ''}` })));
}
