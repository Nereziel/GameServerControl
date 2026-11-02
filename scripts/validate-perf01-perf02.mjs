import fs from 'node:fs';

function read(path) { return fs.readFileSync(path, 'utf8'); }
function requireMatch(condition, message) { if (!condition) throw new Error(message); }

const hub = read('src/GameServerControl.Agent/Hubs/StatusHub.cs');
const logTail = read('src/GameServerControl.Agent/Logs/LogTailService.cs');
const local = read('src/GameServerControl.Agent/Hyperv/LocalProcessService.cs');
const workspace = read('src/GameServerControl.Agent/WebUi/server-workspace.js');

requireMatch(/ServerGroup\s*\(\s*string\s+serverId\s*\)/.test(hub),
  'PERF-01: StatusHub must expose a canonical per-server SignalR group helper.');
requireMatch(/Groups\.AddToGroupAsync\s*\(\s*Context\.ConnectionId\s*,\s*ServerGroup\(serverId\)/.test(hub),
  'PERF-01: SubscribeConsole must join the validated server group.');

for (const [name, source] of [['LogTailService', logTail], ['LocalProcessService', local]]) {
  requireMatch(!/Clients\.All\.SendAsync\s*\(\s*["']logLine["']/.test(source),
    `PERF-01: ${name} still broadcasts logLine through Clients.All.`);
  requireMatch(/Clients\.Group\s*\(\s*StatusHub\.ServerGroup\(/.test(source),
    `PERF-01: ${name} must publish logLine only to the selected server group.`);
}

const renderConsoleAt = workspace.indexOf('async function renderServerConsole');
const readOnlyAt = workspace.indexOf('if (readOnly)', renderConsoleAt);
const subscribeAt = workspace.indexOf("await connection.invoke('SubscribeConsole', [def.id]);", renderConsoleAt);
requireMatch(renderConsoleAt >= 0 && readOnlyAt > renderConsoleAt,
  'PERF-01: could not locate read-only console branch.');
requireMatch(subscribeAt > renderConsoleAt && subscribeAt < readOnlyAt,
  'PERF-01: every live console connection must subscribe before the read-only/stdin branch split.');

console.log('PERF-01 SignalR server-group contract OK.');
