// Runs the online service on this computer for testing, with no extra software: Node's built-in SQLite stands in for
// Cloudflare's D1 database, and the website files are served from public/.
//   node --no-warnings online/dev/local.mjs [port] [database file]      (shop key from POS_SHOP_KEY)
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { DatabaseSync } from 'node:sqlite';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { handle } from '../src/worker.js';

const root = fileURLToPath(new URL('..', import.meta.url));

/** The parts of Cloudflare's D1 API the service uses, over SQLite. */
export function localD1(file = ':memory:') {
  const db = new DatabaseSync(file);
  db.exec('PRAGMA journal_mode = WAL;');
  const statement = (sql, args = []) => ({
    bind: (...values) => statement(sql, values),
    async first(column) {
      const row = db.prepare(sql).get(...args);
      return row === undefined ? null : column ? row[column] : { ...row };
    },
    async all() { return { results: db.prepare(sql).all(...args).map(r => ({ ...r })), success: true }; },
    async run() { const r = db.prepare(sql).run(...args); return { success: true, meta: { changes: r.changes } }; },
    runSync() { return db.prepare(sql).run(...args); },
  });
  return {
    prepare: sql => statement(sql),
    async batch(list) {
      db.exec('BEGIN');
      try {
        const results = list.map(s => s.runSync());
        db.exec('COMMIT');
        return results;
      } catch (e) {
        db.exec('ROLLBACK');
        throw e;
      }
    },
    async exec(sql) { db.exec(sql); },
    close: () => db.close(),
  };
}

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.json': 'application/json', '.txt': 'text/plain; charset=utf-8' };

/** Static files from public/, as Cloudflare would serve them (index.html for folders; unknown pages → 404.html). */
const assets = {
  async fetch(request) {
    let path = decodeURIComponent(new URL(request.url).pathname);
    if (path.endsWith('/')) path += 'index.html';
    const file = normalize(join(root, 'public', path));
    if (!file.startsWith(join(root, 'public'))) return new Response('Not found', { status: 404 });
    try {
      return new Response(await readFile(file), { headers: { 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream' } });
    } catch {
      try {
        return new Response(await readFile(join(root, 'public', `${path}.html`)), { headers: { 'Content-Type': TYPES['.html'] } });
      } catch {
        return new Response('Not found', { status: 404 });
      }
    }
  },
};

/** Starts the service; returns { url, env, close }. */
export async function start({ port = 0, file = ':memory:', shopKey = process.env.POS_SHOP_KEY ?? 'local-test-shop-key-0123456789abcdef', postcodeService } = {}) {
  const DB = localD1(file);
  await DB.exec(await readFile(join(root, 'schema.sql'), 'utf8'));
  const env = { DB, ASSETS: assets, SHOP_KEY: shopKey, POSTCODE_SERVICE: postcodeService };
  const server = createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const request = new Request(`http://${req.headers.host}${req.url}`, {
      method: req.method, headers: req.headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : Buffer.concat(chunks),
    });
    const response = await handle(request, env);
    res.writeHead(response.status, Object.fromEntries(response.headers));
    res.end(Buffer.from(await response.arrayBuffer()));
  });
  await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  return { url, env, close: () => new Promise(resolve => { server.close(resolve); DB.close(); }) };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const { url } = await start({ port: Number(process.argv[2] ?? 8787), file: process.argv[3] ?? ':memory:' });
  console.log(`Online ordering running at ${url}`);
}
