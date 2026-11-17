import assert from 'node:assert/strict';
import fs from 'node:fs';

// Production Cloudflare deploys must be one-click after the repository's
// non-secret Actions Variables have been configured once.
const source = fs.readFileSync('.github/workflows/cloudflare-deploy.yml', 'utf8');
const dispatchStart = source.indexOf('on:\n');
const permissionsStart = source.indexOf('\npermissions:\n', dispatchStart);
assert.ok(dispatchStart >= 0 && permissionsStart > dispatchStart, 'workflow trigger must be explicit');
const trigger = source.slice(dispatchStart, permissionsStart);
assert.match(trigger, /\n  workflow_dispatch:\s*(?:\n|$)/, 'manual dispatch must be enabled');
assert.doesNotMatch(trigger, /\binputs\s*:/, 'manual deploy must not require retyping hostnames or Access identifiers');

for (const [name, key] of [
  ['PANEL_HOSTNAME', 'GSC_CF_PANEL_HOSTNAME'],
  ['GATEWAY_ORIGIN', 'GSC_CF_GATEWAY_ORIGIN'],
  ['ACCESS_TEAM_DOMAIN', 'GSC_CF_ACCESS_TEAM_DOMAIN'],
  ['ACCESS_POLICY_AUD', 'GSC_CF_ACCESS_POLICY_AUD']
]) {
  assert.ok(source.includes(name + ': $' + '{{ vars.' + key + ' }}'),
    'non-secret ' + name + ' must come from a persistent Actions Variable ' + key);
  const start=source.indexOf('for name in ');
  const end=source.indexOf('; do',start);
  const required=start>=0&&end>start ? source.slice(start,end) : '';
  assert.ok(required.includes(name),'missing-value preflight must validate '+name);
}
assert.doesNotMatch(source, /^        env:\s*\n        run:/m,
  'removing dispatch-specific env must not leave empty YAML env mappings');
assert.doesNotMatch(source, /inputs\.(?:panel_hostname|gateway_origin|access_team_domain|access_policy_aud)/,
  'no lingering dispatch-input references');
assert.match(source, /GSC_CF_PANEL_HOSTNAME/, 'missing-variable guidance should name Variables');
for (const key of [
  'CLOUDFLARE_API_TOKEN', 'CLOUDFLARE_ACCOUNT_ID',
  'GSC_CF_HOSTS_DB_ID', 'GSC_CF_HOSTS_ENCRYPTION_KEY',
  'GSC_CF_GATEWAY_SHARED_KEY', 'GSC_CF_ACCESS_ADMIN_EMAILS'
]) {
  assert.ok(source.includes('secrets.' + key), 'Cloudflare secrets must remain in Secrets: ' + key);
}
assert.match(source, /concurrency:\s*\n[\s\S]*?cancel-in-progress:\s*false/,
  'one-click deploy must serialize production deployments');
assert.ok(source.includes('git fetch --no-tags --depth=1 origin refs/heads/main'),
  'deployment must check current main SHA, not rerun a stale source');
assert.ok(source.includes('GITHUB_REF') && source.includes('GITHUB_SHA'),
  'only main head is allowed to deploy');
const lastStaleGuard = source.lastIndexOf('git fetch --no-tags --depth=1 origin refs/heads/main');
const actualDeploy = source.indexOf('npx wrangler deploy --strict');
assert.ok(lastStaleGuard >= 0 && actualDeploy > lastStaleGuard,
  'stale reruns must be rejected before the real deployment');
console.log('Cloudflare one-click deployment contract OK.');
