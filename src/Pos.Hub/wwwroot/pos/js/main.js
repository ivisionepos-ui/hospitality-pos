// Start-up: find out whether this till is registered and someone is logged in, then show the right screen.
import { api, ApiError, credentials, onAuthLost } from './api.js';
import { h, mount, toast } from './dom.js';
import { LiveConnection } from './live.js';
import { loginScreen, pairScreen, setupScreen } from './screens.js';
import { Till } from './till.js';

const root = document.getElementById('app');
let live = null;
let till = null;

function stopLive() {
  live?.stop();
  live = null;
}

function closeDialogs() {
  document.getElementById('dialogs').replaceChildren();
}

async function boot() {
  stopLive();
  closeDialogs();
  try {
    await api.get('/api/health', { noSession: true });
  } catch {
    mount(root, h('div', { class: 'gate' }, h('div', { class: 'gate-card' },
      h('h1', { class: 'gate-title', text: 'Cannot reach the hub' }),
      h('p', { class: 'gate-sub', text: 'Check that the hub PC is on and the hub is running, and that this till is on the shop network.' }),
      h('button', { class: 'btn btn-primary btn-big', onclick: boot, text: 'Try again' }))));
    return;
  }

  if (!credentials.device) {
    const setup = await api.get('/api/setup', { noSession: true });
    const onHubPc = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
    if (!setup.isSetUp && onHubPc) setupScreen(root, boot);
    else pairScreen(root, boot, setup.isSetUp ? null : 'The shop is not set up yet. Do the first-time set-up on the hub PC first.');
    return;
  }

  if (!credentials.session) {
    loginScreen(root, boot);
    return;
  }

  let me;
  try {
    me = await api.get('/api/auth/me');
  } catch (e) {
    if (e instanceof ApiError && (e.status === 401 || e.status === 403)) return; // handled by onAuthLost
    toast(e.message, 'error');
    return;
  }

  if (!me.permissions.includes('TakeOrders')) {
    mount(root, h('div', { class: 'gate' }, h('div', { class: 'gate-card' },
      h('h1', { class: 'gate-title', text: `Hello ${me.staff.name}` }),
      h('p', { class: 'gate-sub', text: 'Your role cannot take orders on a till. The kitchen screen is coming in a later update.' }),
      h('button', { class: 'btn btn-big', onclick: logout, text: 'Log out' }))));
    return;
  }

  till = new Till(root, { me, device: credentials.device, logout });
  await till.start();
  live = new LiveConnection(event => till?.onEvent(event), connected => till?.setLive(connected));
  live.start();
}

let loggingOut = false;

async function logout() {
  loggingOut = true;
  try { await api.post('/api/auth/logout'); } catch { /* the session ends anyway */ }
  loggingOut = false;
  credentials.session = null;
  till = null;
  boot();
}

onAuthLost(
  () => {
    if (!credentials.session || loggingOut) return;
    credentials.session = null;
    till = null;
    toast('Please log in again.', 'info');
    boot();
  },
  () => {
    credentials.device = null;
    credentials.session = null;
    till = null;
    toast('This till is no longer registered with the hub.', 'error', 8000);
    boot();
  });

boot();
