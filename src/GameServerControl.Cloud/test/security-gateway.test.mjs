import test from 'node:test';
import assert from 'node:assert/strict';
import {
  resolveAccessRole,
  agentTokenForRole,
  authorizeGatewayRequest
} from '../src/index.js';

const env = {
  ACCESS_ADMIN_EMAILS: 'admin@example.com; OPS@example.com\nowner@example.com',
  AGENT_ADMIN_TOKEN: 'admin-agent-token',
  AGENT_READONLY_TOKEN: 'readonly-agent-token'
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
  assert.equal(agentTokenForRole('Admin', env), 'admin-agent-token');
  assert.equal(agentTokenForRole('ReadOnly', env), 'readonly-agent-token');
  assert.throws(() => agentTokenForRole('Admin', { ...env, AGENT_ADMIN_TOKEN: '' }), /AGENT_ADMIN_TOKEN/);
  assert.throws(() => agentTokenForRole('ReadOnly', { ...env, AGENT_READONLY_TOKEN: '' }), /AGENT_READONLY_TOKEN/);
});

test('ReadOnly users cannot mutate through the cloud gateway', () => {
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    assert.deepEqual(authorizeGatewayRequest(method, 'ReadOnly'), {
      allowed: false,
      status: 403,
      error: 'Read-only access cannot modify GameServerControl.'
    });
  }
  assert.equal(authorizeGatewayRequest('GET', 'ReadOnly').allowed, true);
  assert.equal(authorizeGatewayRequest('POST', 'Admin').allowed, true);
});
