// Talking to the hub. The till's device key and the staff login are sent with every request.
import { store } from './dom.js';

const DEVICE_KEY = 'pos.device';
const SESSION_KEY = 'pos.session';

/** A refusal or failure from the hub, with its code and message. */
export class ApiError extends Error {
  constructor(status, code, message, problems) {
    super(message);
    this.status = status;
    this.code = code;
    this.problems = problems ?? [];
  }
}

export const credentials = {
  get device() { return store.get(DEVICE_KEY); },
  set device(value) { value ? store.set(DEVICE_KEY, value) : store.remove(DEVICE_KEY); },
  get session() { return store.get(SESSION_KEY); },
  set session(value) { value ? store.set(SESSION_KEY, value) : store.remove(SESSION_KEY); },
};

let onSignedOut = () => {};
let onUnregistered = () => {};

/** What to do when the hub says the login ended, or the till is no longer registered. */
export function onAuthLost(signedOut, unregistered) {
  onSignedOut = signedOut;
  onUnregistered = unregistered;
}

async function request(method, path, body, options = {}) {
  const headers = { Accept: 'application/json' };
  const device = credentials.device;
  if (device) {
    headers['X-Device-Id'] = device.deviceId;
    headers['X-Device-Key'] = device.deviceKey;
  }

  const session = credentials.session;
  if (session && !options.noSession) headers.Authorization = `Bearer ${session.token}`;
  if (options.managerPin) headers['X-Manager-Pin'] = options.managerPin;
  const isFile = body instanceof Blob;
  if (body !== undefined) headers['Content-Type'] = isFile ? 'text/csv' : 'application/json';

  let response;
  try {
    response = await fetch(path, { method, headers, body: body === undefined || isFile ? body : JSON.stringify(body), cache: 'no-store' });
  } catch {
    throw new ApiError(0, 'Offline', 'Cannot reach the hub. Check the network and that the hub PC is on.');
  }

  if (options.raw && response.ok) return response;
  if (response.status === 204) return null;
  const type = response.headers.get('Content-Type') ?? '';
  const data = type.includes('json') ? await response.json() : null;
  if (response.ok) return data;

  const error = new ApiError(response.status, data?.code ?? `Http${response.status}`, data?.message ?? `The hub refused the request (${response.status}).`, data?.problems);
  if (error.code === 'NotLoggedIn' || error.code === 'SessionExpired') onSignedOut(error);
  if (error.code === 'DeviceNotRegistered') onUnregistered(error);
  throw error;
}

export const api = {
  get: (path, options) => request('GET', path, undefined, options),
  post: (path, body = {}, options) => request('POST', path, body, options),
  put: (path, body, options) => request('PUT', path, body, options),
  del: (path, options) => request('DELETE', path, undefined, options),
  /** Sends a file (e.g. an address file) as the request body. */
  upload: (path, file) => request('POST', path, file),
  /** Fetches a file (e.g. a receipt preview) as a blob. */
  async blob(path) {
    const response = await request('GET', path, undefined, { raw: true });
    return response.blob();
  },
};
