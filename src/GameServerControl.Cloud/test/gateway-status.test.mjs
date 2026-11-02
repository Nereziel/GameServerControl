import test from 'node:test';
import assert from 'node:assert/strict';
import { fetchAgentHealth } from '../src/index.js';

const env = {
  AGENT_ORIGIN: 'https://gsc-origin.example.com',
  AGENT_API_TOKEN: 'agent-token-value',
  ORIGIN_ACCESS_CLIENT_ID: 'service-client-id',
  ORIGIN_ACCESS_CLIENT_SECRET: 'service-client-secret'
};

test('fetchAgentHealth authenticates to the Tunnel origin and Agent', async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (input, init = {}) => {
    assert.equal(String(input), 'https://gsc-origin.example.com/api/health');
    const headers = new Headers(init.headers);
    assert.equal(headers.get('authorization'), 'Bearer agent-token-value');
    assert.equal(headers.get('cf-access-client-id'), 'service-client-id');
    assert.equal(headers.get('cf-access-client-secret'), 'service-client-secret');
    return Response.json({ ok: true, ts: '2026-11-02T10:00:00Z' });
  };

  try {
    assert.deepEqual(await fetchAgentHealth(env), {
      ok: true,
      status: 200,
      ts: '2026-11-02T10:00:00Z'
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('fetchAgentHealth reports a protected or unavailable origin without exposing its body', async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response('access denied', { status: 403 });

  try {
    assert.deepEqual(await fetchAgentHealth(env), {
      ok: false,
      status: 403,
      ts: null
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});
