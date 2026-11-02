import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');
const endpoints = read('src/GameServerControl.Agent/Config/AgentSettingsEndpoints.cs');
const relayUi = read('src/GameServerControl.Agent/WebUi/jobs-mods.js');

const failures = [];
const requireText = (source, needle, label) => {
  if (!source.includes(needle)) failures.push(`Missing ${label}: ${needle}`);
};

requireText(endpoints, 'api.MapGet("/settings/public-relay"', 'global Relay settings GET endpoint');
requireText(endpoints, 'api.MapPut("/settings/public-relay"', 'global Relay settings PUT endpoint');
requireText(relayUi, 'id="tabSettings"', 'top-level Settings tab');
requireText(relayUi, 'id="settingsView"', 'top-level Settings view');
requireText(relayUi, '/api/settings/public-relay', 'Settings UI Relay API usage');
requireText(relayUi, 'JSON.stringify({enabled,publicPorts})', 'per-server Relay-only save payload');

for (const legacyId of ['swRelayUrl', 'swRelayToken', 'swRelayTarget', 'swRelayPublicAddress']) {
  if (relayUi.includes(legacyId)) failures.push(`Per-server Relay UI still owns global field: ${legacyId}`);
}

if (failures.length) {
  console.error('Global Settings contract failures:');
  for (const failure of failures) console.error(` - ${failure}`);
  process.exit(1);
}

console.log('Global Settings ownership contract OK.');
