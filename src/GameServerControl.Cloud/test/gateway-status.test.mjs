import test from 'node:test';
import assert from 'node:assert/strict';
import { SignJWT, exportJWK, generateKeyPair } from 'jose';
import worker, { fetchAgentHealth } from '../src/index.js';
import {
  gatewayUrl, validateHostRegistration, validateGatewayConfig,
  saveHost, listHosts, getHost, deleteHost, hostOverview
} from '../src/hosts.js';

class FakeD1 {
  rows = new Map();
  prepare(sql) {
    const db = this;
    return {
      args: [],
      bind(...args) { this.args = args; return this; },
      async first() {
        if (sql.includes('COUNT(*)')) return { total: db.rows.size };
        if (sql.includes('FROM registered_hosts WHERE id = ?'))
          return db.rows.get(this.args[0]) || null;
        throw Error('Unexpected D1 first: ' + sql);
      },
      async all() {
        if (sql.includes('SELECT id, name FROM registered_hosts'))
          return { results: [...db.rows.values()].map(({ id, name }) => ({ id, name })) };
        throw Error('Unexpected D1 all: ' + sql);
      },
      async run() {
        if (sql.startsWith('INSERT')) {
          const [id, name, admin_secret, readonly_secret] = this.args;
          if (db.rows.has(id)) throw Error('UNIQUE constraint failed');
          db.rows.set(id, { id, name, admin_secret, readonly_secret });
        } else if (sql.startsWith('DELETE')) db.rows.delete(this.args[0]);
        else throw Error('Unexpected D1 run: ' + sql);
        return { success: true };
      }
    };
  }
}

const tokenAdmin = 'test-admin-token-0123456789ABCDEF';
const tokenViewer = 'test-readonly-token-0123456789AB';
function setupEnv() {
  return {
    HOSTS_DB: new FakeD1(),
    HOSTS_ENCRYPTION_KEY: btoa(String.fromCharCode(...Uint8Array.from({length:32}, (_,i)=>i+1))),
    GATEWAY_ORIGIN: 'https://gateway.example.com',
    GATEWAY_SHARED_KEY: 'isolated-vps-gateway-secret-0123456789ABCDEF',
    ACCESS_TEAM_DOMAIN: 'https://team.cloudflareaccess.com',
    ACCESS_POLICY_AUD: 'test-audience',
    ACCESS_ADMIN_EMAILS: 'owner@example.com',
    ASSETS: { fetch: async () => new Response('static UI') }
  };
}

test('only a fixed HTTPS VPS origin and scoped Agent API paths are permitted', () => {
  const env = setupEnv();
  assert.doesNotThrow(() => validateGatewayConfig(env));
  const link = gatewayUrl(env, 'pc2', '/api/servers/ark/start', '?x=1&access_token=hidden');
  assert.equal(link.toString(), 'https://gateway.example.com/gsc-agent/pc2/api/servers/ark/start?x=1');
  for (const path of ['/api/hosts', '/api/hosts/pc1/api/tokens', '/health',
      '/api/../secret', '//evil.example.com/api/servers', '/hubs/unexpected']) {
    assert.throws(() => gatewayUrl(env, 'pc1', path));
  }
  assert.throws(() => gatewayUrl(env, 'local', '/api/health'));
  assert.throws(() => gatewayUrl({ ...env, GATEWAY_ORIGIN: 'http://gateway.example.com' }, 'pc1', '/api/health'));
  assert.throws(() => gatewayUrl({ ...env, GATEWAY_ORIGIN: 'https://127.0.0.1' }, 'pc1', '/api/health'));
});

test('Cloud D1 credential storage encrypts tokens, lists only metadata and rejects duplicate IDs', async () => {
  const env = setupEnv();
  const input = { id: 'pc2', name: 'Gaming PC 2', adminToken: tokenAdmin, readOnlyToken: tokenViewer };
  await saveHost(env, input);
  const stored = JSON.stringify([...env.HOSTS_DB.rows.values()]);
  assert.equal(stored.includes(tokenAdmin), false);
  assert.equal(stored.includes(tokenViewer), false);
  assert.deepEqual(await listHosts(env), [{ id: 'pc2', name: 'Gaming PC 2', isLocal: false }]);
  const decrypted = await getHost(env, 'pc2');
  assert.equal(decrypted.adminToken, tokenAdmin);
  assert.equal(decrypted.readOnlyToken, tokenViewer);
  await assert.rejects(() => saveHost(env, input), /UNIQUE constraint/);
  assert.equal(await deleteHost(env, 'pc2'), true);
  assert.equal(await getHost(env, 'pc2'), null);
  assert.throws(() => validateHostRegistration({ ...input, id: 'local' }));
  assert.throws(() => validateHostRegistration({ ...input, readOnlyToken: tokenAdmin }));
});

test('the Worker routes each host separately; a stopped PC never blocks another', async () => {
  const env = setupEnv();
  let pc1Online = true;
  const calls = [];
  const { publicKey, privateKey } = await generateKeyPair('RS256');
  const jwk = await exportJWK(publicKey);
  jwk.kid = 'test-key';
  jwk.alg = 'RS256';
  jwk.use = 'sig';

  const issueToken = email => new SignJWT({ email })
    .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
    .setIssuedAt().setExpirationTime('10m')
    .setIssuer(env.ACCESS_TEAM_DOMAIN)
    .setAudience(env.ACCESS_POLICY_AUD)
    .sign(privateKey);

  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (input, init = {}) => {
    const url = new URL(String(input));
    if (url.pathname === '/cdn-cgi/access/certs')
      return Response.json({ keys: [jwk] });
    assert.equal(url.hostname, 'gateway.example.com');
    assert.match(url.pathname, /^\/gsc-agent\/(pc1|pc2)\//);
    const pc = url.pathname.includes('/pc1/') ? 'pc1' : 'pc2';
    if (pc === 'pc1' && !pc1Online) throw Error('Computer offline');
    const headers = new Headers(init.headers);
    assert.equal(headers.get('x-gsc-proxy-key'), env.GATEWAY_SHARED_KEY);
    const bearer = headers.get('authorization');
    calls.push({ pc, path: url.pathname, bearer, method: init.method || 'GET' });
    const role = bearer === 'Bearer ' + tokenAdmin ? 'Admin'
      : bearer === 'Bearer ' + tokenViewer ? 'ReadOnly' : null;
    if (!role) return new Response('Unauthorized', { status: 401 });
    if (url.pathname.endsWith('/api/auth/me')) return Response.json({ role });
    if (url.pathname.endsWith('/api/servers')) return Response.json([
      { id: pc + '-game', name: pc + ' server', gameType: 'Valheim',
        hostingMode: 'BareMetal', rconPassword: 'should-not-be-leaked' }
    ]);
    if (url.pathname.endsWith('/api/status')) return Response.json([
      { id: pc + '-game', processState: 'Running', cpuPercent: 5,
        memoryMB: 128, pidInGuest: 111 }
    ]);
    if (url.pathname.endsWith('/api/health')) return Response.json({ ok: true, ts: 'now' });
    if (url.pathname.endsWith('/api/servers/pc2-game/start'))
      return Response.json({ success: role === 'Admin' }, { status: role === 'Admin' ? 200 : 403 });
    return new Response('Not Found', { status: 404 });
  };

  const request = async (pathname, token, method = 'GET', body = undefined, origin = 'https://panel.example.com') => {
    const headers = { 'cf-access-jwt-assertion': token, origin };
    if (body) headers['content-type'] = 'application/json';
    return worker.fetch(new Request('https://panel.example.com' + pathname, {
      method, headers, body: body ? JSON.stringify(body) : undefined
    }), env);
  };

  try {
    const adminJWT = await issueToken('owner@example.com');
    const readonlyJWT = await issueToken('viewer@example.com');
    for (const id of ['pc1','pc2']) {
      const added = await request('/api/hosts', adminJWT, 'POST',
        { id, name: id.toUpperCase(), adminToken: tokenAdmin, readOnlyToken: tokenViewer });
      assert.equal(added.status, 201);
      const body = await added.text();
      assert.doesNotMatch(body, /token-|example.com/);
    }
    const denied = await request('/api/hosts', readonlyJWT, 'POST',
      { id: 'pc3', name: 'Another', adminToken: tokenAdmin, readOnlyToken: tokenViewer });
    assert.equal(denied.status, 403);
    const me = await (await request('/api/auth/me', readonlyJWT)).json();
    assert.equal(me.role, 'ReadOnly');
    const before = await (await request('/api/hosts/overview', readonlyJWT)).json();
    assert.equal(before.length, 2);
    assert.ok(before.every(item => item.online));
    assert.doesNotMatch(JSON.stringify(before), /rconPassword|should-not-be-leaked|token-/);

    pc1Online = false;
    const after = await (await request('/api/hosts/overview', readonlyJWT)).json();
    assert.equal(after.find(item => item.id === 'pc1').online, false);
    assert.equal(after.find(item => item.id === 'pc2').online, true);
    const remoteRead = await request('/api/hosts/pc2/api/servers', readonlyJWT);
    assert.equal(remoteRead.status, 200);
    const deniedWrite = await request('/api/hosts/pc2/api/servers/pc2-game/start', readonlyJWT, 'POST');
    assert.equal(deniedWrite.status, 403);
    const allowedWrite = await request('/api/hosts/pc2/api/servers/pc2-game/start', adminJWT, 'POST');
    assert.equal(allowedWrite.status, 200);
    assert.equal(calls.at(-1).pc, 'pc2');
    assert.equal(calls.at(-1).bearer, 'Bearer ' + tokenAdmin);
    const crossOrigin = await request('/api/hosts/pc2/api/servers/pc2-game/start', adminJWT, 'POST',
      undefined, 'https://bad.example');
    assert.equal(crossOrigin.status, 403);
    const noJWT = await worker.fetch(new Request('https://panel.example.com/api/hosts'), env);
    assert.equal(noJWT.status, 403);
    const health = await fetchAgentHealth(env);
    assert.equal(health.ok, false); // first host is intentionally offline
  } finally {
    globalThis.fetch = originalFetch;
  }
});
