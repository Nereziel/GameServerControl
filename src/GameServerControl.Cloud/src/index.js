import { createRemoteJWKSet, jwtVerify } from 'jose';

let jwks = null;
let jwksFor = '';

const WRITE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

function jsonError(status, error, detail = null) {
  return Response.json(detail ? { error, detail } : { error }, {
    status,
    headers: { 'cache-control': 'no-store' }
  });
}

function normalizedOrigin(value) {
  const url = new URL(String(value || ''));
  if (url.protocol !== 'https:') throw new Error('AGENT_ORIGIN must use https://');
  url.pathname = '/';
  url.search = '';
  url.hash = '';
  return url;
}

function requiredConfig(env) {
  const missing = [];
  for (const key of [
    'AGENT_ORIGIN',
    'AGENT_API_TOKEN',
    'ORIGIN_ACCESS_CLIENT_ID',
    'ORIGIN_ACCESS_CLIENT_SECRET',
    'ACCESS_TEAM_DOMAIN',
    'ACCESS_POLICY_AUD'
  ]) {
    if (!env[key]) missing.push(key);
  }
  return missing;
}

async function verifyPanelAccess(request, env) {
  const token = request.headers.get('cf-access-jwt-assertion');
  if (!token) throw new Error('Cloudflare Access JWT is missing.');

  const issuer = String(env.ACCESS_TEAM_DOMAIN || '').replace(/\/+$/, '');
  if (!issuer.startsWith('https://')) throw new Error('ACCESS_TEAM_DOMAIN must use https://');

  if (!jwks || jwksFor !== issuer) {
    jwks = createRemoteJWKSet(new URL(`${issuer}/cdn-cgi/access/certs`));
    jwksFor = issuer;
  }

  const { payload } = await jwtVerify(token, jwks, {
    issuer,
    audience: env.ACCESS_POLICY_AUD
  });

  return payload;
}

function isGatewayPath(pathname) {
  return pathname === '/api' || pathname.startsWith('/api/') || pathname.startsWith('/hubs/');
}

function hasValidBrowserOrigin(request) {
  if (!WRITE_METHODS.has(request.method)) return true;
  const requestUrl = new URL(request.url);
  return request.headers.get('origin') === requestUrl.origin;
}

export async function fetchAgentHealth(env) {
  const target = new URL('/api/health', normalizedOrigin(env.AGENT_ORIGIN));
  const headers = new Headers();
  headers.set('authorization', `Bearer ${env.AGENT_API_TOKEN}`);
  headers.set('cf-access-client-id', env.ORIGIN_ACCESS_CLIENT_ID);
  headers.set('cf-access-client-secret', env.ORIGIN_ACCESS_CLIENT_SECRET);
  headers.set('accept', 'application/json');

  let response;
  try {
    response = await fetch(target.toString(), {
      method: 'GET',
      headers,
      redirect: 'manual'
    });
  } catch {
    return { ok: false, status: 0, ts: null };
  }

  if (!response.ok) return { ok: false, status: response.status, ts: null };

  let body;
  try { body = await response.json(); }
  catch { return { ok: false, status: response.status, ts: null }; }

  return {
    ok: body?.ok === true,
    status: response.status,
    ts: typeof body?.ts === 'string' ? body.ts : null
  };
}

function relayWebSocket(upstreamResponse) {
  const upstream = upstreamResponse.webSocket;
  if (!upstream) return upstreamResponse;

  const pair = new WebSocketPair();
  const client = pair[0];
  const server = pair[1];

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

async function proxyToAgent(request, env, accessPayload) {
  const incoming = new URL(request.url);
  const origin = normalizedOrigin(env.AGENT_ORIGIN);
  const target = new URL(origin);
  target.pathname = incoming.pathname;
  target.search = incoming.search;
  target.searchParams.delete('access_token');

  const headers = new Headers(request.headers);
  headers.delete('authorization');
  headers.delete('cookie');
  headers.delete('cf-access-jwt-assertion');
  headers.delete('cf-access-authenticated-user-email');
  headers.set('authorization', `Bearer ${env.AGENT_API_TOKEN}`);
  headers.set('cf-access-client-id', env.ORIGIN_ACCESS_CLIENT_ID);
  headers.set('cf-access-client-secret', env.ORIGIN_ACCESS_CLIENT_SECRET);
  headers.set('x-gameservercontrol-gateway', 'cloudflare-worker');

  const remoteUser = accessPayload?.email || accessPayload?.sub;
  if (remoteUser) headers.set('x-gameservercontrol-remote-user', String(remoteUser));

  const init = {
    method: request.method,
    headers,
    redirect: 'manual'
  };
  if (request.method !== 'GET' && request.method !== 'HEAD') init.body = request.body;

  let upstreamResponse;
  try {
    upstreamResponse = await fetch(new Request(target.toString(), init));
  } catch (error) {
    return jsonError(502, 'Agent origin is unreachable.', error instanceof Error ? error.message : String(error));
  }

  if (upstreamResponse.webSocket) return relayWebSocket(upstreamResponse);

  const responseHeaders = new Headers(upstreamResponse.headers);
  responseHeaders.set('cache-control', 'no-store');
  responseHeaders.delete('set-cookie');

  return new Response(upstreamResponse.body, {
    status: upstreamResponse.status,
    statusText: upstreamResponse.statusText,
    headers: responseHeaders
  });
}

async function authenticatedAccess(request, env) {
  try {
    return { payload: await verifyPanelAccess(request, env), error: null };
  } catch (error) {
    return {
      payload: null,
      error: jsonError(403, 'Cloudflare Access authentication failed.', error instanceof Error ? error.message : String(error))
    };
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/gateway/health') {
      return Response.json({
        ok: true,
        mode: 'cloudflare',
        configured: requiredConfig(env).length === 0
      }, { headers: { 'cache-control': 'no-store' } });
    }

    if (url.pathname === '/gateway/status') {
      const missing = requiredConfig(env);
      if (missing.length) return jsonError(503, 'Cloud gateway is not configured.', missing.join(', '));

      const access = await authenticatedAccess(request, env);
      if (access.error) return access.error;

      const agent = await fetchAgentHealth(env);
      const remoteUser = access.payload?.email || access.payload?.sub || null;
      return Response.json({
        ok: agent.ok,
        mode: 'cloudflare',
        user: remoteUser ? String(remoteUser) : null,
        agent: {
          reachable: agent.ok,
          status: agent.status,
          timestamp: agent.ts
        }
      }, {
        status: agent.ok ? 200 : 503,
        headers: { 'cache-control': 'no-store' }
      });
    }

    if (isGatewayPath(url.pathname)) {
      const missing = requiredConfig(env);
      if (missing.length) return jsonError(503, 'Cloud gateway is not configured.', missing.join(', '));

      if (!hasValidBrowserOrigin(request))
        return jsonError(403, 'Cross-origin state-changing requests are not allowed.');

      const access = await authenticatedAccess(request, env);
      if (access.error) return access.error;

      return proxyToAgent(request, env, access.payload);
    }

    return env.ASSETS.fetch(request);
  }
};
