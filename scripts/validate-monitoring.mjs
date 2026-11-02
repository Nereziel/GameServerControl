import fs from 'node:fs';

const read = p => fs.readFileSync(p, 'utf8');
const required = (text, token, message) => {
  if (!text.includes(token)) throw new Error(`${message}: missing ${token}`);
};

const models = read('src/GameServerControl.Shared/Models.cs');
const tracker = read('src/GameServerControl.Agent/Servers/StatusTracker.cs');
const endpoints = read('src/GameServerControl.Agent/Servers/OperationalEndpoints.cs');
const ui = read('src/GameServerControl.Agent/WebUi/config-backups.js') + '\n' + read('src/GameServerControl.Agent/WebUi/server-admin.js');

required(models, 'ServerStatusSample', 'Shared model must define status samples');
required(tracker, 'MaxHistoryPerServer', 'Status history must have an explicit bound');
required(tracker, 'History(', 'StatusTracker must expose bounded history');
required(endpoints, '/history', 'Agent must expose server history');
required(ui, 'openStats', 'Web UI must retain its monitoring view');
required(ui, '/history?limit=90', 'Stats view must preload Agent-side status history');
required(ui, 'maxSamples', 'Stats view must surface the bounded server-side history limit');
required(ui, 'STAT_POINTS.length > 90', 'Client-side graph must also remain bounded');

console.log('Monitoring history contract OK.');
