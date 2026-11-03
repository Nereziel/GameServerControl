import fs from 'node:fs';

function read(path) { return fs.readFileSync(path, 'utf8'); }
function requireMatch(condition, message) { if (!condition) throw new Error(message); }

const hub = read('src/GameServerControl.Agent/Hubs/StatusHub.cs');
const logTail = read('src/GameServerControl.Agent/Logs/LogTailService.cs');
const local = read('src/GameServerControl.Agent/Hyperv/LocalProcessService.cs');
const workspace = read('src/GameServerControl.Agent/WebUi/server-workspace.js');
const installer = read('src/GameServerControl.Agent/Servers/ServerInstaller.cs');

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
const renderConsoleEnd = workspace.indexOf('async function serverWorkspaceSendConsole', renderConsoleAt);
requireMatch(renderConsoleAt >= 0 && renderConsoleEnd > renderConsoleAt,
  'PERF-01: could not locate the live-console render function.');
const renderConsole = workspace.slice(renderConsoleAt, renderConsoleEnd);
const subscribeAt = renderConsole.indexOf("await connection.invoke('SubscribeConsole', [def.id]);");
const storeConnectionAt = renderConsole.indexOf('SERVER_WORKSPACE.console = connection;');
requireMatch(subscribeAt >= 0,
  'PERF-01: every live console connection must subscribe to its validated server group.');
requireMatch(storeConnectionAt > subscribeAt,
  'PERF-01: the live console connection must subscribe before it is retained by the workspace.');
requireMatch(!/\/logs\/tail/.test(renderConsole),
  'PERF-01: LogOnly UI lifecycle must stay on the scoped SignalR connection instead of global HTTP tail control.');

requireMatch(/private\s+DateTimeOffset\s+_nextPruneAt/.test(installer),
  'PERF-02: GET-side retention cleanup must be rate-limited instead of scanning every request.');
requireMatch(/public\s+InstallProgress\?\s+Get\([\s\S]*?PruneCompletedJobs\(\);/.test(installer),
  'PERF-02: job reads must periodically trigger TTL cleanup.');
requireMatch(/public\s+string\s+StartJob\([\s\S]*?PruneCompletedJobs\(force:\s*true\);/.test(installer),
  'PERF-02: starting a job must force retention cleanup and cap enforcement.');
requireMatch(/if\s*\(finished\)\s*PruneCompletedJobs\(force:\s*true\)/.test(installer),
  'PERF-02: completing a job must force retention cleanup and cap enforcement.');
requireMatch(/private\s+void\s+PruneCompletedJobs\(bool\s+force\s*=\s*false\)[\s\S]*?if\s*\(!force\s*&&\s*now\s*<\s*_nextPruneAt\)\s*return/.test(installer),
  'PERF-02: non-forced cleanup must have a time gate to avoid O(n log n) work on every GET.');

console.log('PERF-01 SignalR server-group and PERF-02 pruning contract OK.');
