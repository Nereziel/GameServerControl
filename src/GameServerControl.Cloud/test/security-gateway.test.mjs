import test from 'node:test';
import assert from 'node:assert/strict';
import {
  resolveAccessRole,
  agentTokenForRole,
  authorizeGatewayRequest,
  hasValidBrowserOrigin,
  withBrowserSecurityHeaders
} from '../src/index.js';

const env = {
  ACCESS_ADMIN_EMAILS: 'admin@example.com; OPS@example.com\nowner@example.com',
  ACCESS_TEAM_DOMAIN: 'https://team.cloudflareaccess.com',
  ACCESS_POLICY_AUD: 'unit-audience'
};

test('verified Access email maps to Admin case-insensitively', () => {
  assert.equal(resolveAccessRole({ email: 'Admin@Example.com' }, env), 'Admin');
  assert.equal(resolveAccessRole({ email: 'ops@example.com' }, env), 'Admin');
});

test('Access users outside the explicit admin allowlist fail closed to ReadOnly', () => {
  assert.equal(resolveAccessRole({ email: 'viewer@example.com' }, env), 'ReadOnly');
  assert.equal(resolveAccessRole({ sub: 'access-user-id' }, env), 'ReadOnly');
  assert.equal(resolveAccessRole({}, { ...env, ACCESS_ADMIN_EMAILS: '' }), 'ReadOnly');
});

test('gateway selects a different Agent token for each role', () => {
  const host = { adminToken: 'agent-admin-a', readOnlyToken: 'agent-readonly-b' };
  assert.equal(agentTokenForRole('Admin', host), 'agent-admin-a');
  assert.equal(agentTokenForRole('ReadOnly', host), 'agent-readonly-b');
  assert.throws(() => agentTokenForRole('Admin', { readOnlyToken: 'a' }), /adminToken/);
  assert.throws(() => agentTokenForRole('ReadOnly', { adminToken: 'b' }), /readOnlyToken/);
});

test('ReadOnly users cannot mutate through the cloud gateway', () => {
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    assert.deepEqual(authorizeGatewayRequest(method, 'ReadOnly', '/api/servers/example'), {
      allowed: false,
      status: 403,
      error: 'Read-only access cannot modify GameServerControl.'
    });
  }
  assert.equal(authorizeGatewayRequest('GET', 'ReadOnly', '/api/servers').allowed, true);
  assert.equal(authorizeGatewayRequest('POST', 'Admin', '/api/hosts/pc1/api/servers/example/start').allowed, true);
});

test('ReadOnly users can negotiate SignalR and control read-only live-log subscriptions', () => {
  assert.equal(authorizeGatewayRequest('POST', 'ReadOnly', '/api/hosts/pc1/hubs/status/negotiate').allowed, true);
  assert.equal(authorizeGatewayRequest('POST', 'ReadOnly', '/api/hosts/pc1/api/servers/example/logs/tail').allowed, true);
  assert.equal(authorizeGatewayRequest('POST', 'ReadOnly', '/api/servers/example/start').allowed, false);
  assert.equal(authorizeGatewayRequest('POST', 'ReadOnly', '/api/hosts/pc1/api/servers/example/rcon/command').allowed, false);
});

test('browser writes and WebSocket upgrades require the panel origin', () => {
  const samePost = new Request('https://panel.example.com/api/servers/example/start', {
    method: 'POST',
    headers: { origin: 'https://panel.example.com' }
  });
  const crossPost = new Request('https://panel.example.com/api/servers/example/start', {
    method: 'POST',
    headers: { origin: 'https://evil.example.net' }
  });
  const sameWs = new Request('https://panel.example.com/hubs/status?id=abc', {
    headers: { origin: 'https://panel.example.com', upgrade: 'websocket' }
  });
  const crossWs = new Request('https://panel.example.com/hubs/status?id=abc', {
    headers: { origin: 'https://evil.example.net', upgrade: 'websocket' }
  });
  const plainGet = new Request('https://panel.example.com/api/status');

  assert.equal(hasValidBrowserOrigin(samePost), true);
  assert.equal(hasValidBrowserOrigin(crossPost), false);
  assert.equal(hasValidBrowserOrigin(sameWs), true);
  assert.equal(hasValidBrowserOrigin(crossWs), false);
  assert.equal(hasValidBrowserOrigin(plainGet), true);
});

test('static WebUI responses receive a CSP that disables inline script execution', async () => {
  const response = withBrowserSecurityHeaders(new Response('<!doctype html>', {
    headers: { 'content-type': 'text/html; charset=utf-8' }
  }));
  const csp = response.headers.get('content-security-policy') || '';
  assert.match(csp, /script-src 'self'/);
  assert.match(csp, /script-src-attr 'none'/);
  assert.match(csp, /object-src 'none'/);
  assert.match(csp, /frame-ancestors 'none'/);
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(response.headers.get('referrer-policy'), 'no-referrer');
  assert.equal(response.headers.get('x-frame-options'), 'DENY');
  assert.equal(await response.text(), '<!doctype html>');
});
