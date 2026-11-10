// The Cloudflare Worker is the only multi-host coordinator. No Agent acts as
// an upstream for another Agent; every request goes directly to its selected
// host through the fixed HTTPS VPS gateway.
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();
export const HOST_ID = /^[a-z][a-z0-9-]{0,39}$/;
const MAX_HOSTS = 20;

function bytesToBase64(bytes) {
  let text = '';
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text);
}

function base64ToBytes(text) {
  const raw = atob(text);
  return Uint8Array.from(raw, char => char.charCodeAt(0));
}

export function validateGatewayConfig(env) {
  if (!env.HOSTS_DB || typeof env.HOSTS_DB.prepare !== 'function')
    throw new Error('HOSTS_DB binding is missing.');
  const origin = new URL(String(env.GATEWAY_ORIGIN || ''));
  if (origin.protocol !== 'https:' || origin.username || origin.password ||
      origin.pathname !== '/' || origin.search || origin.hash ||
      !/^[a-z0-9.-]+$/i.test(origin.hostname) ||
      origin.hostname === 'localhost' || !origin.hostname.includes('.') ||
      origin.port)
    throw new Error('GATEWAY_ORIGIN must be a public HTTPS hostname without a path or port.');
  if (String(env.GATEWAY_SHARED_KEY || '').length < 32)
    throw new Error('GATEWAY_SHARED_KEY must contain at least 32 random characters.');
  const key = base64ToBytes(String(env.HOSTS_ENCRYPTION_KEY || ''));
  if (key.length !== 32)
    throw new Error('HOSTS_ENCRYPTION_KEY must be 32 random bytes encoded in base64.');
  return origin;
}

async function aesKey(env) {
  const bytes = base64ToBytes(String(env.HOSTS_ENCRYPTION_KEY));
  return crypto.subtle.importKey('raw', bytes, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
}

export async function protectHostCredentials(env, value) {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ciphertext = await crypto.subtle.encrypt({ name: 'AES-GCM', iv },
    await aesKey(env), textEncoder.encode(value));
  return 'v1:' + bytesToBase64(iv) + ':' + bytesToBase64(new Uint8Array(ciphertext));
}

export async function unprotectHostCredentials(env, value) {
  const parts = String(value || '').split(':');
  if (parts.length !== 3 || parts[0] !== 'v1')
    throw new Error('Unsupported host credential envelope.');
  const decrypted = await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: base64ToBytes(parts[1]) },
    await aesKey(env), base64ToBytes(parts[2]));
  return textDecoder.decode(decrypted);
}

export function validateHostRegistration(value) {
  const id = String(value?.id || '').trim();
  const name = String(value?.name || '').trim();
  const adminToken = String(value?.adminToken || '').trim();
  const readOnlyToken = String(value?.readOnlyToken || '').trim();
  if (!HOST_ID.test(id) || id === 'local' || id === 'all')
    throw new Error('Host ID must be lowercase letters, numbers or hyphens (not local/all).');
  if (name.length < 1 || name.length > 80)
    throw new Error('Host display name must be 1–80 characters.');
  if (adminToken.length < 24 || adminToken.length > 1024 ||
      readOnlyToken.length < 24 || readOnlyToken.length > 1024 ||
      adminToken === readOnlyToken)
    throw new Error('Distinct Admin and ReadOnly tokens (24–1024 characters) are required.');
  // No user-supplied remote URL is accepted. Only routes statically configured
  // on the VPS under this ID can ever be targeted.
  return { id, name, adminToken, readOnlyToken };
}

export function gatewayUrl(env, hostId, apiPath, query = '') {
  if (!HOST_ID.test(hostId) || hostId === 'all' || hostId === 'local')
    throw new Error('Invalid host ID.');
  if (!/^\/(api\/|hubs\/status(?:\/negotiate)?$)/.test(apiPath) ||
      apiPath.startsWith('/api/hosts/') || apiPath === '/api/hosts' ||
      apiPath.includes('\\') || apiPath.includes('//') ||
      apiPath.split('/').some(segment => segment === '..' || segment === '.') ||
      /[?#\0]/.test(apiPath))
    throw new Error('Invalid Agent API path.');
  const origin = validateGatewayConfig(env);
  const url = new URL(origin);
  url.pathname = '/gsc-agent/' + hostId + apiPath;
  url.search = query;
  url.searchParams.delete('access_token');
  return url;
}

export async function listHosts(env) {
  const { results } = await env.HOSTS_DB.prepare(
    'SELECT id, name FROM registered_hosts ORDER BY name, id').all();
  return (results || []).map(({ id, name }) => ({ id, name, isLocal: false }));
}

export async function getHost(env, id) {
  if (!HOST_ID.test(id)) return null;
  const item = await env.HOSTS_DB.prepare(
    'SELECT id, name, admin_secret, readonly_secret FROM registered_hosts WHERE id = ?')
    .bind(id).first();
  if (!item) return null;
  return {
    id: item.id,
    name: item.name,
    adminToken: await unprotectHostCredentials(env, item.admin_secret),
    readOnlyToken: await unprotectHostCredentials(env, item.readonly_secret)
  };
}

export async function saveHost(env, input) {
  const host = validateHostRegistration(input);
  const count = await env.HOSTS_DB.prepare('SELECT COUNT(*) AS total FROM registered_hosts').first();
  if (Number(count?.total || 0) >= MAX_HOSTS) throw new Error('Maximum of 20 computers reached.');
  const admin = await protectHostCredentials(env, host.adminToken);
  const viewer = await protectHostCredentials(env, host.readOnlyToken);
  await env.HOSTS_DB.prepare(
    'INSERT INTO registered_hosts (id, name, admin_secret, readonly_secret) VALUES (?, ?, ?, ?)')
    .bind(host.id, host.name, admin, viewer).run();
  return { id: host.id, name: host.name, isLocal: false };
}

export async function deleteHost(env, id) {
  if (!HOST_ID.test(id)) return false;
  const item = await env.HOSTS_DB.prepare('SELECT id FROM registered_hosts WHERE id = ?')
    .bind(id).first();
  if (!item) return false;
  await env.HOSTS_DB.prepare('DELETE FROM registered_hosts WHERE id = ?').bind(id).run();
  return true;
}

export function gatewayHeaders(env, agentToken, remoteUser, webSocket = false) {
  const headers = new Headers({
    authorization: 'Bearer ' + agentToken,
    'x-gsc-proxy-key': String(env.GATEWAY_SHARED_KEY),
    'x-gameservercontrol-gateway': 'cloudflare-worker',
    accept: 'application/json'
  });
  if (remoteUser) headers.set('x-gameservercontrol-remote-user', String(remoteUser).slice(0, 320));
  if (webSocket) headers.set('upgrade', 'websocket');
  return headers;
}

export async function fetchFromHost(env, host, role, apiPath, init = {}, query = '') {
  const target = gatewayUrl(env, host.id, apiPath, query);
  const webSocket = new Headers(init.headers || {}).get('upgrade') === 'websocket';
  const headers = gatewayHeaders(env,
    role === 'Admin' ? host.adminToken : host.readOnlyToken,
    init.remoteUser, webSocket);
  for (const field of ['content-type', 'range', 'if-none-match', 'if-match']) {
    const value = new Headers(init.headers || {}).get(field);
    if (value) headers.set(field, value);
  }
  return fetch(target.toString(), {
    method: init.method || 'GET', body: init.body, headers,
    redirect: 'manual', signal: init.signal
  });
}

export async function verifyHostTokens(env, host) {
  for (const [role, token] of [['Admin', host.adminToken], ['ReadOnly', host.readOnlyToken]]) {
    const headers = gatewayHeaders(env, token, null);
    const response = await fetch(gatewayUrl(env, host.id, '/api/auth/me'), {
      headers, redirect: 'manual', signal: AbortSignal.timeout(7000)
    });
    if (!response.ok) throw new Error('VPS route or remote Agent token unavailable (' + role + ').');
    const me = await response.json();
    if (String(me?.role || '').toLowerCase() !== role.toLowerCase())
      throw new Error('Remote token does not have the ' + role + ' role.');
  }
}

export async function hostOverview(env, host) {
  try {
    const [defs, stats] = await Promise.all([
      fetchFromHost(env, host, 'ReadOnly', '/api/servers', {
        signal: AbortSignal.timeout(7000)
      }),
      fetchFromHost(env, host, 'ReadOnly', '/api/status', {
        signal: AbortSignal.timeout(7000)
      })
    ]);
    if (!defs.ok || !stats.ok) throw new Error('Remote Agent unavailable.');
    const [serverDefs, statuses] = await Promise.all([defs.json(), stats.json()]);
    if (!Array.isArray(serverDefs) || !Array.isArray(statuses))
      throw new Error('Invalid Agent response.');
    const statusById = new Map(statuses.map(x => [x.id, x]));
    const servers = serverDefs.slice(0, 200).map(def => {
      const status = statusById.get(def.id) || {};
      return {
        id: String(def.id || ''),
        name: String(def.name || ''),
        gameType: String(def.gameType || ''),
        hostingMode: String(def.hostingMode || ''),
        processState: String(status.processState || 'Unknown'),
        cpuPercent: typeof status.cpuPercent === 'number' ? status.cpuPercent : null,
        memoryMB: typeof status.memoryMB === 'number' ? status.memoryMB : null,
        pidInGuest: Number.isInteger(status.pidInGuest) ? status.pidInGuest : null,
        lastBackupAt: status.lastBackupAt || null,
        hasError: !!status.lastError
      };
    }).filter(x => x.id);
    return { id: host.id, name: host.name, isLocal: false, online: true, error: null, servers };
  } catch {
    return { id: host.id, name: host.name, isLocal: false, online: false,
      error: 'Agent unavailable.', servers: [] };
  }
}
