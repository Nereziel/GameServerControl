import { createRemoteJWKSet, jwtVerify } from 'jose';
import {
  validateGatewayConfig, validateHostRegistration, listHosts, getHost,
  saveHost, deleteHost, fetchFromHost, verifyHostTokens, hostOverview
} from './hosts.js';

let jwks = null;
let jwksFor = '';
const ADMIN_ROLE = 'Admin';
const READONLY_ROLE = 'ReadOnly';
const WRITE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const BROWSER_CSP = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; script-src-attr 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self' ws: wss:";

function jsonError(status, error) {
  return Response.json({ error }, { status, headers: { 'cache-control': 'no-store' } });
}

export function withBrowserSecurityHeaders(response) {
  const headers = new Headers(response.headers);
  headers.set('content-security-policy', BROWSER_CSP);
  headers.set('x-content-type-options', 'nosniff');
  headers.set('referrer-policy', 'no-referrer');
  headers.set('x-frame-options', 'DENY');
  headers.set('permissions-policy', 'camera=(), microphone=(), geolocation=()');
  return new Response(response.body, {
    status: response.status, statusText: response.statusText, headers
  });
}

function requiredConfig(env) {
  const missing = ['ACCESS_TEAM_DOMAIN', 'ACCESS_POLICY_AUD', 'GATEWAY_ORIGIN',
    'GATEWAY_SHARED_KEY', 'HOSTS_ENCRYPTION_KEY'].filter(name => !env[name]);
  if (!env.HOSTS_DB) missing.push('HOSTS_DB');
  if (!missing.length) {
    try { validateGatewayConfig(env); }
    catch { missing.push('invalid gateway configuration'); }
  }
  return missing;
}

function adminEmails(env) {
  return new Set(String(env.ACCESS_ADMIN_EMAILS || '')
    .split(/[,;\n\r]+/).map(value => value.trim().toLowerCase()).filter(Boolean));
}

export function resolveAccessRole(payload, env) {
  const email = typeof payload?.email === 'string' ? payload.email.trim().toLowerCase() : '';
  return email && adminEmails(env).has(email) ? ADMIN_ROLE : READONLY_ROLE;
}

// Kept as a pure helper for role tests. Remote host tokens are selected from
// encrypted host records, not Worker-wide Agent secrets.
export function agentTokenForRole(role, tokens) {
  const key = role === ADMIN_ROLE ? 'adminToken' : 'readOnlyToken';
  const value = tokens?.[key];
  if (!value) throw new Error(key + ' is required for the selected computer.');
  return value;
}

function isReadOnlyControlPost(method, pathname) {
  if (String(method).toUpperCase() !== 'POST') return false;
  return /^\/api\/hosts\/[a-z][a-z0-9-]{0,39}\/hubs\/status\/negotiate$/i.test(pathname)
    || /^\/api\/hosts\/[a-z][a-z0-9-]{0,39}\/api\/servers\/[^/]+\/logs\/tail$/i.test(pathname);
}

export function authorizeGatewayRequest(method, role, pathname = '') {
  const normalizedMethod = String(method || '').toUpperCase();
  if (!WRITE_METHODS.has(normalizedMethod) || role === ADMIN_ROLE ||
      isReadOnlyControlPost(normalizedMethod, pathname))
    return { allowed: true, status: 200, error: null };
  return { allowed: false, status: 403,
    error: 'Read-only access cannot modify GameServerControl.' };
}

export function hasValidBrowserOrigin(request) {
  const method = String(request.method || '').toUpperCase();
  const webSocket = String(request.headers.get('upgrade') || '').toLowerCase() === 'websocket';
  if (!WRITE_METHODS.has(method) && !webSocket) return true;
  return request.headers.get('origin') === new URL(request.url).origin;
}

async function verifyPanelAccess(request, env) {
  const token = request.headers.get('cf-access-jwt-assertion');
  if (!token) throw new Error('Cloudflare Access JWT is missing.');
  const issuer = String(env.ACCESS_TEAM_DOMAIN || '').replace(/\/+$/, '');
  if (!issuer.startsWith('https://')) throw new Error('Invalid Access team domain.');
  if (!jwks || jwksFor !== issuer) {
    jwks = createRemoteJWKSet(new URL(issuer + '/cdn-cgi/access/certs'));
    jwksFor = issuer;
  }
  const { payload } = await jwtVerify(token, jwks, {
    issuer, audience: env.ACCESS_POLICY_AUD
  });
  return payload;
}

function isGatewayPath(pathname) {
  return pathname === '/api' || pathname.startsWith('/api/') || pathname.startsWith('/hubs/');
}

function relayWebSocket(upstreamResponse) {
  const upstream = upstreamResponse.webSocket;
  if (!upstream) return upstreamResponse;
  const pair = new WebSocketPair();
  const client = pair[0], server = pair[1];
  upstream.binaryType = 'arraybuffer';
  server.binaryType = 'arraybuffer';
  upstream.accept();
  server.accept();

  const pipe = (from, to) => {
    from.addEventListener('message', event => {
      try { to.send(event.data); }
      catch { try { from.close(1011, 'Peer connection closed'); } catch {} }
    });
    from.addEventListener('close', event => {
      const code = event.code === 1005 || event.code === 1006 ? 1000 : event.code;
      try { to.close(code, event.reason || 'Connection closed'); } catch {}
    });
    from.addEventListener('error', () => {
      try { to.close(1011, 'WebSocket relay error'); } catch {}
    });
  };
  pipe(server, upstream);
  pipe(upstream, server);
  const headers = new Headers();
  const protocol = upstreamResponse.headers.get('sec-websocket-protocol');
  if (protocol) headers.set('sec-websocket-protocol', protocol);
  return new Response(null, { status: 101, webSocket: client, headers });
}

export async function fetchAgentHealth(env) {
  try {
    const hosts = await listHosts(env);
    if (!hosts.length) return { ok: false, status: 0, ts: null };
    const host = await getHost(env, hosts[0].id);
    if (!host) return { ok: false, status: 0, ts: null };
    const response = await fetchFromHost(env, host, 'ReadOnly', '/api/health', {
      signal: AbortSignal.timeout(7000)
    });
    if (!response.ok) return { ok: false, status: response.status, ts: null };
    const body = await response.json();
    return { ok: body?.ok === true, status: response.status, ts: body?.ts || null };
  } catch {
    return { ok: false, status: 0, ts: null };
  }
}

async function proxyToAgent(request, env, user, role, hostId, suffix) {
  let host;
  try { host = await getHost(env, hostId); }
  catch { return jsonError(503, 'Stored host credentials are unavailable.'); }
  if (!host) return jsonError(404, 'Computer is not registered.');

  const incoming = new URL(request.url);
  const upstreamPath = '/' + suffix;
  const headers = new Headers();
  for (const field of ['content-type', 'accept', 'range', 'if-none-match', 'if-match', 'upgrade']) {
    const value = request.headers.get(field);
    if (value) headers.set(field, value);
  }
  const webSocket = request.headers.get('upgrade')?.toLowerCase() === 'websocket';
  if (webSocket && upstreamPath !== '/hubs/status')
    return jsonError(400, 'Only the Agent status hub supports WebSocket upgrades.');

  let upstream;
  try {
    upstream = await fetchFromHost(env, host, role, upstreamPath, {
      method: request.method,
      headers,
      body: ['GET', 'HEAD'].includes(request.method) ? undefined : request.body,
      remoteUser: user?.email || user?.sub,
      signal: webSocket ? undefined : AbortSignal.timeout(180000)
    }, incoming.search);
  } catch {
    return jsonError(502, 'Selected Agent is unreachable through the VPS gateway.');
  }

  if (upstream.webSocket) return relayWebSocket(upstream);
  const responseHeaders = new Headers({ 'cache-control': 'no-store' });
  for (const field of ['content-type', 'content-disposition', 'etag',
      'last-modified', 'content-range', 'accept-ranges']) {
    const value = upstream.headers.get(field);
    if (value) responseHeaders.set(field, value);
  }
  return new Response(upstream.body, {
    status: upstream.status, statusText: upstream.statusText, headers: responseHeaders
  });
}

async function handleManagedHosts(request, env, role, user, pathname) {
  if (pathname === '/api/health' && request.method === 'GET')
    return Response.json({ ok: true, ts: new Date().toISOString() },
      { headers: { 'cache-control': 'no-store' } });

  if (pathname === '/api/auth/me' && request.method === 'GET')
    return Response.json({
      id: 'cloudflare', name: String(user?.email || user?.sub || 'Cloudflare'),
      role
    }, { headers: { 'cache-control': 'no-store' } });

  if (pathname === '/api/hosts' && request.method === 'GET')
    return Response.json(await listHosts(env), { headers: { 'cache-control': 'no-store' } });

  if (pathname === '/api/hosts/overview' && request.method === 'GET') {
    const hosts = await listHosts(env);
    const results = await Promise.all(hosts.map(async metadata => {
      try {
        const credentials = await getHost(env, metadata.id);
        if (!credentials) throw new Error('No host credentials');
        return await hostOverview(env, credentials);
      } catch {
        return { ...metadata, online: false, servers: [], error: 'Computer unavailable.' };
      }
    }));
    return Response.json(results, { headers: { 'cache-control': 'no-store' } });
  }

  if (pathname === '/api/hosts' && request.method === 'POST') {
    if (role !== ADMIN_ROLE) return jsonError(403, 'Admin access is required.');
    let input;
    try { input = validateHostRegistration(await request.json()); }
    catch (error) { return jsonError(400, String(error?.message || 'Invalid computer details.')); }
    try {
      await verifyHostTokens(env, input);
      const created = await saveHost(env, input);
      return Response.json(created, {
        status: 201, headers: { 'cache-control': 'no-store' }
      });
    } catch (error) {
      const message = String(error?.message || '');
      if (message.includes('UNIQUE constraint') || message.includes('SQLITE_CONSTRAINT'))
        return jsonError(409, 'Computer ID already exists.');
      if (message.includes('Maximum of 20')) return jsonError(409, message);
      return jsonError(400, 'Cannot register host. Check its VPS route and both Agent token roles.');
    }
  }

  const deleteMatch = pathname.match(/^\/api\/hosts\/([a-z][a-z0-9-]{0,39})$/);
  if (deleteMatch && request.method === 'DELETE') {
    if (role !== ADMIN_ROLE) return jsonError(403, 'Admin access is required.');
    return (await deleteHost(env, deleteMatch[1]))
      ? new Response(null, { status: 204 })
      : jsonError(404, 'Computer is not registered.');
  }

  const match = pathname.match(/^\/api\/hosts\/([a-z][a-z0-9-]{0,39})\/(api\/.*|hubs\/status(?:\/negotiate)?)$/);
  if (match) return proxyToAgent(request, env, user, role, match[1], match[2]);
  return jsonError(404, 'Unknown cloud endpoint. Select a computer first.');
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/gateway/health')
      return Response.json({
        ok: true, mode: 'cloudflare-multi-host', configured: requiredConfig(env).length === 0
      }, { headers: { 'cache-control': 'no-store' } });

    if (!isGatewayPath(url.pathname) && url.pathname !== '/gateway/status')
      return withBrowserSecurityHeaders(await env.ASSETS.fetch(request));

    const missing = requiredConfig(env);
    if (missing.length)
      return jsonError(503, 'Cloud gateway is not configured: ' + missing.join(', '));
    if (!hasValidBrowserOrigin(request))
      return jsonError(403, 'Cross-origin state-changing requests are not allowed.');

    let user;
    try { user = await verifyPanelAccess(request, env); }
    catch { return jsonError(403, 'Cloudflare Access authentication failed.'); }
    const role = resolveAccessRole(user, env);

    if (url.pathname === '/gateway/status') {
      const hosts = await listHosts(env);
      const results = await Promise.all(hosts.map(async item => {
        try { return await hostOverview(env, await getHost(env, item.id)); }
        catch { return { online: false }; }
      }));
      return Response.json({
        ok: true, mode: 'cloudflare-multi-host',
        user: user.email || user.sub || null, role,
        hosts: hosts.length, online: results.filter(item => item.online).length
      }, { headers: { 'cache-control': 'no-store' } });
    }

    const authorization = authorizeGatewayRequest(request.method, role, url.pathname);
    if (!authorization.allowed) return jsonError(authorization.status, authorization.error);
    try { return await handleManagedHosts(request, env, role, user, url.pathname); }
    catch { return jsonError(503, 'Cloud host storage or gateway operation failed.'); }
  }
};
