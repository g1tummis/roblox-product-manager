'use strict';
// Roblox Product Manager: a zero-dependency server (Node 18+).
//
// OAuth 2.0 roles (https://create.roblox.com/docs/cloud/auth/oauth2-overview):
//   Resource owner       = the player who signs in
//   Client               = this server (a *confidential* client: the secret never leaves it)
//   Authorization server = https://apis.roblox.com/oauth
//   Resource server      = Roblox Open Cloud APIs (game passes, developer products, badges)
//
// Flow used: authorization code + PKCE (S256) + state + nonce, as Roblox recommends.

const http = require('node:http');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const readline = require('node:readline');
const { spawn } = require('node:child_process');

// When built as a single .exe (see build.js), index.html is embedded in the executable.
let sea = null;
try { sea = require('node:sea'); } catch { /* older Node */ }
const IS_SEA = !!(sea && sea.isSea());
const APP_DIR = IS_SEA ? path.dirname(process.execPath) : __dirname;
const SETTINGS_FILE = process.platform === 'win32' && process.env.APPDATA
  ? path.join(process.env.APPDATA, 'RobloxProductManager', 'settings.json')
  : path.join(os.homedir(), '.config', 'roblox-product-manager', 'settings.json');
const DEFAULT_SCOPES = [
  'openid', 'profile',
  'game-pass:read', 'game-pass:write',
  'developer-product:read', 'developer-product:write',
  'legacy-universe.badge:manage-and-spend-robux', // needed to create badges; also allows editing them
].join(' ');

const OAUTH = 'https://apis.roblox.com/oauth/v1';
const SESSION_IDLE_MS = 7 * 24 * 3600 * 1000; // signed-in sessions
const LOGIN_TIMEOUT_MS = 10 * 60 * 1000;       // half-finished sign-ins
const MAX_UPLOAD = 10 * 1024 * 1024;

// Filled in by applyConfig() before the server starts.
let CLIENT_ID, CLIENT_SECRET, PORT, REDIRECT_URI, SCOPES, redirectUrl, CALLBACK_PATH, IS_LOCAL, HOST, SECURE_COOKIE, CONFIG_ERROR;

function applyConfig(c) {
  CLIENT_ID = String(c.clientId || '').trim();
  CLIENT_SECRET = String(c.clientSecret || '').trim();
  PORT = Number(c.port) || 3000;
  REDIRECT_URI = c.redirectUri || `http://localhost:${PORT}/callback`;
  SCOPES = String(c.scopes || DEFAULT_SCOPES).trim();
  redirectUrl = new URL(REDIRECT_URI);
  CALLBACK_PATH = redirectUrl.pathname;
  IS_LOCAL = ['localhost', '127.0.0.1', '[::1]'].includes(redirectUrl.hostname);
  HOST = process.env.HOST || (IS_LOCAL ? '127.0.0.1' : '0.0.0.0');
  SECURE_COOKIE = redirectUrl.protocol === 'https:';
  CONFIG_ERROR =
    !CLIENT_ID || !CLIENT_SECRET ? 'The Roblox Client ID and Client Secret are not set. Run the app in a terminal to enter them, or set ROBLOX_CLIENT_ID and ROBLOX_CLIENT_SECRET.' :
    !SCOPES.split(/\s+/).includes('openid') ? 'SCOPES must include "openid".' : '';
}

// Only these Open Cloud calls can be made through the server.
// The first capture group (if any) is the universe ID, which must be one the user granted.
const ALLOWED = [
  ['GET',   /^\/game-passes\/v1\/universes\/(\d+)\/game-passes\/creator$/],
  ['POST',  /^\/game-passes\/v1\/universes\/(\d+)\/game-passes$/],
  ['PATCH', /^\/game-passes\/v1\/universes\/(\d+)\/game-passes\/\d+$/],
  ['GET',   /^\/developer-products\/v2\/universes\/(\d+)\/developer-products\/creator$/],
  ['POST',  /^\/developer-products\/v2\/universes\/(\d+)\/developer-products$/],
  ['PATCH', /^\/developer-products\/v2\/universes\/(\d+)\/developer-products\/\d+$/],
  ['POST',  /^\/legacy-badges\/v1\/universes\/(\d+)\/badges$/],
  ['PATCH', /^\/legacy-badges\/v1\/badges\/\d+$/],
];

// ---------- page ----------
const PAGE = IS_SEA ? Buffer.from(sea.getAsset('index.html')) : fs.readFileSync(path.join(__dirname, 'index.html'));
const SCRIPT_HASH = (() => {
  const m = /<script>([\s\S]*?)<\/script>/.exec(PAGE.toString('utf8'));
  return m ? `'sha256-${crypto.createHash('sha256').update(m[1].replace(/\r\n?/g, '\n')).digest('base64')}'` : "'none'";
})();
const PAGE_HEADERS = {
  'Content-Type': 'text/html; charset=utf-8',
  'Cache-Control': 'no-store',
  'Content-Security-Policy': `default-src 'none'; script-src ${SCRIPT_HASH}; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`,
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
};

// ---------- sessions (in memory) ----------
const sessions = new Map();
setInterval(() => {
  const now = Date.now();
  for (const [id, s] of sessions) if (now - s.seen > (s.user ? SESSION_IDLE_MS : LOGIN_TIMEOUT_MS)) sessions.delete(id);
}, 10 * 60 * 1000).unref();

const rand = n => crypto.randomBytes(n).toString('base64url');

function getSession(req) {
  const m = /(?:^|;\s*)sid=([\w-]{43})/.exec(req.headers.cookie || '');
  const s = m ? sessions.get(m[1]) : null;
  if (s) s.seen = Date.now();
  return s || null;
}

function createSession(res) {
  const s = { id: rand(32), seen: Date.now() };
  sessions.set(s.id, s);
  res.setHeader('Set-Cookie', `sid=${s.id}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${SESSION_IDLE_MS / 1000}${SECURE_COOKIE ? '; Secure' : ''}`);
  return s;
}

// ---------- OAuth ----------
function startLogin(req, res, url, s) {
  // The session cookie must live on the same host as the redirect URI (e.g. "localhost", not "127.0.0.1").
  if (IS_LOCAL && req.headers.host !== redirectUrl.host) return redirect(res, `${redirectUrl.origin}/login${url.search}`);
  s = s || createSession(res);
  const verifier = rand(48); // 64 chars (PKCE allows 43-128)
  s.login = { state: rand(24), nonce: rand(16), verifier, at: Date.now() };
  const q = new URLSearchParams({
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT_URI,
    scope: SCOPES,
    response_type: 'code',
    state: s.login.state,
    nonce: s.login.nonce,
    code_challenge: crypto.createHash('sha256').update(verifier).digest('base64url'),
    code_challenge_method: 'S256',
  });
  // "Add or remove games" re-opens Roblox's consent screen so the user can change which experiences are shared.
  if (url.searchParams.get('prompt') === 'consent') q.set('prompt', 'consent');
  redirect(res, `${OAUTH}/authorize?${q.toString().replace(/\+/g, '%20')}`);
}

async function finishLogin(req, res, url, s) {
  const q = url.searchParams, login = s && s.login;
  if (!login || !q.get('state') || q.get('state') !== login.state || Date.now() - login.at > LOGIN_TIMEOUT_MS) {
    return redirect(res, '/?error=' + encodeURIComponent('Sign-in expired or did not match. Please try again.'));
  }
  s.login = null;
  if (q.get('error')) {
    const why = q.get('error') === 'access_denied' ? 'Sign-in was cancelled on Roblox.' : (q.get('error_description') || q.get('error'));
    return redirect(res, '/?error=' + encodeURIComponent(why));
  }
  const tokens = await oauth('/token', { grant_type: 'authorization_code', code: q.get('code') || '', code_verifier: login.verifier });
  const claims = decodeJwt(tokens.id_token);
  if (claims.nonce !== login.nonce) throw new HttpError(400, 'Sign-in check failed (nonce mismatch). Please try again.');

  sessions.delete(s.id); // new session id after sign-in
  const ns = createSession(res);
  setTokens(ns, tokens);
  ns.user = { id: claims.sub, name: claims.name || claims.nickname || '', username: claims.preferred_username || '' };
  redirect(res, '/');
}

function setTokens(s, t) {
  s.access = t.access_token;
  s.refresh = t.refresh_token;
  s.exp = Date.now() + (Number(t.expires_in) || 900) * 1000;
}

// Access tokens last 15 minutes; refresh tokens are single-use, so refreshes are serialized per session.
async function accessToken(s) {
  if (Date.now() < s.exp - 60_000) return s.access;
  s.refreshing ||= oauth('/token', { grant_type: 'refresh_token', refresh_token: s.refresh })
    .then(t => setTokens(s, t), e => {
      if (e.upstream === 400 || e.upstream === 401) { sessions.delete(s.id); throw signedOut(); }
      throw e;
    })
    .finally(() => { s.refreshing = null; });
  await s.refreshing;
  return s.access;
}

async function oauth(endpoint, params) {
  const r = await fetch(OAUTH + endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ ...params, client_id: CLIENT_ID, client_secret: CLIENT_SECRET }),
    signal: AbortSignal.timeout(15_000),
  });
  const text = await r.text();
  if (!r.ok) throw Object.assign(new HttpError(502, `Roblox sign-in service error (${r.status}): ${text.slice(0, 200)}`), { upstream: r.status });
  return text ? JSON.parse(text) : {};
}

// Experiences the user ticked on Roblox's consent screen.
async function loadUniverses(s) {
  const info = await oauth('/token/resources', { token: await accessToken(s) });
  const owners = new Map();
  for (const ri of info.resource_infos || []) {
    for (const id of (ri.resources && ri.resources.universe && ri.resources.universe.ids) || []) owners.set(String(id), ri.owner || {});
  }
  const ids = [...owners.keys()], details = new Map();
  for (let i = 0; i < ids.length; i += 50) {
    try {
      const j = await getJson(`https://games.roblox.com/v1/games?universeIds=${ids.slice(i, i + 50).join(',')}`);
      for (const g of j.data || []) details.set(String(g.id), g);
    } catch { /* names are cosmetic; fall back to IDs */ }
  }
  s.universes = ids.map(id => {
    const g = details.get(id), o = owners.get(id);
    return {
      id,
      name: (g && g.name) || `Experience ${id}`,
      ownerType: o.type || (g && g.creator && g.creator.type) || 'User',
      ownerName: (g && g.creator && g.creator.name) || '',
    };
  }).sort((a, b) => a.name.localeCompare(b.name));
  s.allowed = new Set(ids);
  return s.universes;
}

async function checkUniverse(s, id) {
  if (!s.allowed) await loadUniverses(s);
  if (!s.allowed.has(id)) throw new HttpError(403, 'This app was not given access to that game. Use "Add or remove games".');
}

// ---------- API ----------
async function handleApi(req, res, url, s) {
  if (req.headers['x-app'] !== '1') throw new HttpError(403, 'Bad request.');
  if (!s || !s.user) throw signedOut('Not signed in.');
  const p = url.pathname;
  let m;

  if (req.method === 'GET' && p === '/api/session') {
    if (!s.universes) await loadUniverses(s);
    return send(res, 200, { user: s.user, universes: s.universes });
  }
  if (req.method === 'GET' && p === '/api/universes') {
    return send(res, 200, { universes: await loadUniverses(s) });
  }
  if (req.method === 'POST' && p === '/api/logout') {
    sessions.delete(s.id);
    oauth('/token/revoke', { token: s.refresh }).catch(() => {});
    return send(res, 200, { ok: true }, { 'Set-Cookie': `sid=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax${SECURE_COOKIE ? '; Secure' : ''}` });
  }
  // Badge listing and free-quota are public Roblox endpoints (no token sent).
  if (req.method === 'GET' && (m = /^\/api\/badges\/(\d+)$/.exec(p))) {
    await checkUniverse(s, m[1]);
    const q = new URLSearchParams({ limit: '100', sortOrder: 'Asc' });
    if (url.searchParams.get('cursor')) q.set('cursor', url.searchParams.get('cursor'));
    return send(res, 200, await getJson(`https://badges.roblox.com/v1/universes/${m[1]}/badges?${q}`));
  }
  if (req.method === 'GET' && (m = /^\/api\/badge-quota\/(\d+)$/.exec(p))) {
    await checkUniverse(s, m[1]);
    return send(res, 200, await getJson(`https://badges.roblox.com/v1/universes/${m[1]}/free-badges-quota`));
  }
  if (p.startsWith('/api/rbx/')) return proxy(req, res, url, s);
  throw new HttpError(404, 'Not found.');
}

async function proxy(req, res, url, s) {
  const target = url.pathname.slice('/api/rbx'.length);
  const rule = ALLOWED.find(([method, re]) => method === req.method && re.test(target));
  if (!rule) throw new HttpError(404, 'That Roblox action is not allowed.');
  const universe = rule[1].exec(target)[1];
  if (universe) await checkUniverse(s, universe);

  const body = req.method === 'GET' ? undefined : await readBody(req);
  const headers = { Authorization: `Bearer ${await accessToken(s)}` };
  if (req.headers['content-type']) headers['Content-Type'] = req.headers['content-type'];

  let r;
  for (let attempt = 1; ; attempt++) {
    r = await fetch(`https://apis.roblox.com${target}${url.search}`, { method: req.method, headers, body, signal: AbortSignal.timeout(60_000) });
    if (r.status !== 429 || attempt === 3) break;
    await r.body?.cancel();
    await sleep(Math.min(Number(r.headers.get('retry-after')) || attempt * 2, 10) * 1000);
  }
  res.writeHead(r.status, { 'Content-Type': r.headers.get('content-type') || 'application/json', 'Cache-Control': 'no-store' });
  res.end(Buffer.from(await r.arrayBuffer()));
}

// ---------- helpers ----------
class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
const signedOut = (msg = 'Your sign-in expired. Please sign in again.') => Object.assign(new HttpError(401, msg), { signedOut: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));

function send(res, status, data, headers = {}) {
  const text = typeof data === 'string';
  res.writeHead(status, { 'Content-Type': text ? 'text/plain; charset=utf-8' : 'application/json', 'Cache-Control': 'no-store', ...headers });
  res.end(text ? data : JSON.stringify(data));
}

function redirect(res, location) {
  res.writeHead(302, { Location: location, 'Cache-Control': 'no-store' });
  res.end();
}

async function getJson(url) {
  const r = await fetch(url, { signal: AbortSignal.timeout(15_000) });
  if (!r.ok) throw new HttpError(502, `Roblox returned ${r.status} (${new URL(url).hostname}).`);
  return r.json();
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', c => { size += c.length; if (size <= MAX_UPLOAD) chunks.push(c); });
    req.on('end', () => size > MAX_UPLOAD ? reject(new HttpError(413, 'Upload too large (10 MB max).')) : resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

function decodeJwt(token) {
  try { return JSON.parse(Buffer.from(String(token).split('.')[1], 'base64url').toString('utf8')); } catch { return {}; }
}

function loadEnv(file) {
  if (!fs.existsSync(file)) return;
  for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/.exec(line);
    if (m && !(m[1] in process.env)) process.env[m[1]] = m[2].replace(/^(['"])(.*)\1$/, '$2');
  }
}

// ---------- server ----------
async function handle(req, res) {
  const url = new URL(req.url, 'http://localhost');
  const isCallback = req.method === 'GET' && url.pathname === CALLBACK_PATH && (url.searchParams.has('code') || url.searchParams.has('error'));
  try {
    if (CONFIG_ERROR) return send(res, 503, `Setup needed: ${CONFIG_ERROR}\nSee README.md.`);
    const s = getSession(req);
    if (isCallback) return await finishLogin(req, res, url, s);
    if (req.method === 'GET' && url.pathname === '/') return res.writeHead(200, PAGE_HEADERS).end(PAGE);
    if (req.method === 'GET' && url.pathname === '/login') return startLogin(req, res, url, s);
    if (url.pathname.startsWith('/api/')) return await handleApi(req, res, url, s);
    send(res, 404, 'Not found');
  } catch (e) {
    let status = e.status || 500, message = e.message;
    if (e.name === 'TimeoutError') { status = 504; message = 'Roblox took too long to respond. Try again.'; }
    else if (!e.status && e instanceof TypeError) { status = 502; message = 'Could not reach Roblox. Check your connection and try again.'; }
    else if (status === 500) console.error(e);
    if (res.headersSent) return res.end();
    if (isCallback) return redirect(res, '/?error=' + encodeURIComponent(message));
    send(res, status, { error: message, signedOut: !!e.signedOut });
  }
}

function startServer(desktop) {
  const server = http.createServer(handle);
  server.on('error', async e => {
    if (e.code !== 'EADDRINUSE') return fatal(e);
    if (await isOurApp()) {
      console.log(`\nThe app is already running in another window. Opening ${redirectUrl.origin}`);
      if (desktop) openBrowser(redirectUrl.origin);
      return pauseThenExit(0);
    }
    fatal(new Error(`Port ${PORT} is already used by another program.\nStart the app again and press S to choose another port, then add the new Redirect URL to your OAuth app on Roblox.`));
  });
  server.listen(PORT, HOST, () => {
    if (CONFIG_ERROR) console.warn(`Setup needed: ${CONFIG_ERROR}`);
    console.log(`\nRoblox Product Manager is running at ${redirectUrl.origin}`);
    console.log(`Redirect URL in use: ${REDIRECT_URI}`);
    if (desktop) {
      console.log('\nKeep this window open while you use the app. Close it to stop the app.');
      openBrowser(redirectUrl.origin);
    }
  });
}

async function isOurApp() {
  try {
    const r = await fetch(redirectUrl.origin + '/', { signal: AbortSignal.timeout(2000) });
    return (await r.text()).includes('<title>Roblox Product Manager</title>');
  } catch { return false; }
}

function openBrowser(url) {
  if (process.argv.includes('--no-browser')) return;
  const [cmd, args] =
    process.platform === 'win32' ? ['cmd', ['/c', 'start', '', url]] :
    process.platform === 'darwin' ? ['open', [url]] : ['xdg-open', [url]];
  try { spawn(cmd, args, { stdio: 'ignore', detached: true, windowsHide: true }).on('error', () => {}).unref(); } catch { /* user can open it by hand */ }
}

// ---------- first-run setup (console prompts) ----------
const interactive = () => !!(process.stdin.isTTY && process.stdout.isTTY);

function readSettings() {
  try { return JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8')); } catch { return null; }
}

function saveSettings(cfg) {
  fs.mkdirSync(path.dirname(SETTINGS_FILE), { recursive: true });
  fs.writeFileSync(SETTINGS_FILE, JSON.stringify(cfg, null, 2), { mode: 0o600 });
}

function ask(label, { def = '', hidden = false } = {}) {
  return new Promise(resolve => {
    const query = `${label}${def ? ` [${def}]` : ''}: `;
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: true });
    if (hidden) rl._writeToOutput = () => rl.output.write(`\x1b[2K\r${query}${'*'.repeat(rl.line.length)}`);
    rl.question(query, answer => {
      rl.close();
      if (hidden) process.stdout.write('\n');
      resolve(answer.trim() || def);
    });
  });
}

// Resolves with the key pressed, or '' after `ms` milliseconds.
function waitForKey(ms) {
  return new Promise(resolve => {
    const stdin = process.stdin;
    let timer;
    const done = key => {
      clearTimeout(timer);
      stdin.removeListener('data', onData);
      stdin.setRawMode(false);
      stdin.pause();
      resolve(key);
    };
    const onData = d => {
      const key = d.toString();
      if (key === '\u0003') process.exit(0); // Ctrl+C
      done(key);
    };
    stdin.setRawMode(true);
    stdin.resume();
    stdin.on('data', onData);
    timer = setTimeout(done, ms, '');
  });
}

async function setup(old) {
  console.log([
    '',
    '=== Roblox Product Manager: setup ===',
    'You need the Client ID and Client Secret of your OAuth 2.0 app. Get them here:',
    '  https://create.roblox.com/dashboard/credentials?activeTab=OAuthTab',
    'README.md (Part 1) lists the scopes to tick on that app.',
    '',
  ].join('\n'));

  let clientId = '';
  while (!clientId) {
    clientId = await ask('Client ID', { def: old.clientId });
    if (!/^\S+$/.test(clientId)) { console.log('  Paste the Client ID (no spaces).'); clientId = ''; }
  }

  let clientSecret = '';
  while (!clientSecret) {
    const label = old.clientSecret ? 'Client Secret (hidden; press Enter to keep the saved one)' : 'Client Secret (hidden)';
    clientSecret = (await ask(label, { hidden: true })) || old.clientSecret || '';
    if (!clientSecret) console.log('  The Client Secret is required.');
    else if (/\s/.test(clientSecret)) { console.log('  The Client Secret has no spaces. Paste it again.'); clientSecret = ''; }
  }
  console.log(`  Secret received (ends with ...${clientSecret.slice(-4)}).`);
  if (!clientSecret.startsWith('RBX-')) console.log('  Note: Roblox secrets usually start with "RBX-". Double-check it if sign-in fails.');

  let port = 0;
  while (!port) {
    port = Number(await ask('Port', { def: String(old.port || 3000) }));
    if (!Number.isInteger(port) || port < 1 || port > 65535) { console.log('  Enter a whole number from 1 to 65535.'); port = 0; }
  }

  const cfg = { ...old, clientId, clientSecret, port };
  if (old.port !== port) delete cfg.redirectUri; // a custom redirect URI only applies to the port it was set for
  saveSettings(cfg);
  console.log([
    '',
    `Saved to ${SETTINGS_FILE}`,
    'Make sure this exact Redirect URL is added to your OAuth app on Roblox:',
    `  ${cfg.redirectUri || `http://localhost:${port}/callback`}`,
  ].join('\n'));
  return cfg;
}

function pauseThenExit(code) {
  if (!interactive()) process.exit(code);
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  rl.question('\nPress Enter to close this window.', () => process.exit(code));
}

function fatal(e) {
  console.error(`\nError: ${e && e.message ? e.message : e}`);
  pauseThenExit(1);
}

// ---------- start ----------
async function main() {
  process.title = 'Roblox Product Manager';
  loadEnv(path.join(APP_DIR, '.env'));
  const env = process.env;

  // 1) Environment variables / .env (for developers and hosting).
  if (env.ROBLOX_CLIENT_ID && env.ROBLOX_CLIENT_SECRET) {
    applyConfig({ clientId: env.ROBLOX_CLIENT_ID, clientSecret: env.ROBLOX_CLIENT_SECRET, port: env.PORT, redirectUri: env.REDIRECT_URI, scopes: env.SCOPES });
    return startServer(IS_SEA);
  }

  // 2) Saved settings, or 3) ask for them in the console.
  let cfg = readSettings();
  if (interactive()) {
    const complete = cfg && cfg.clientId && cfg.clientSecret;
    if (!complete || process.argv.includes('--setup')) {
      cfg = await setup(cfg || {});
    } else {
      console.log(`Using saved settings: Client ID ${cfg.clientId}, port ${cfg.port || 3000}.`);
      console.log('Press S to change them, or any other key to start now (starting in 4 seconds)...');
      if (/^s$/i.test(await waitForKey(4000))) cfg = await setup(cfg);
    }
  }
  applyConfig(cfg || {});
  startServer(IS_SEA || interactive());
}

main().catch(fatal);
