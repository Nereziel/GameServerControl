import assert from 'node:assert/strict';
import fs from 'node:fs';

const read = path => fs.readFileSync(path, 'utf8');
const worker = read('src/GameServerControl.Runner/RunnerWorker.cs');
const host = read('src/GameServerControl.Runner/RunnerProcessHost.cs');
const manager = read('src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs');
const backup = read('src/GameServerControl.Agent/Servers/BackupService.cs');
const orch = read('src/GameServerControl.Agent/Servers/ServerOrchestrator.cs');
const installer = read('src/GameServerControl.Agent/Servers/ServerInstaller.cs');

assert.match(worker, /if \(_manifest\.AutoStart\)/, 'Runner boot autostart must remain enabled by manifest');
assert.match(worker, /attempt <= 3/, 'Boot launch must have bounded retries');
assert.match(worker, /MonitorProcessAsync/, 'Runner must monitor game process exits');
assert.match(worker, /attempts >= 3/, 'Crash recovery must be capped');
assert.match(host, /_restartDesired = false;/, 'Manual stop must disable automatic restart');
assert.match(host, /public bool RestartDesired => _restartDesired/, 'Recovery must honor stop intent');
assert.match(host, /_cleanExitObserved/, 'Clean process exits must not trigger crash recovery');
assert.match(worker, /TimeSpan.FromMinutes\(10\)/, 'Crash retry budget must reset after sustained uptime');
assert.match(manager, /startMode = enabled \? "auto" : "demand"/, 'Windows service must start after boot when enabled');

assert.match(backup, /CreatePreUpdateAsync/, 'Backups must have a dedicated pre-update path');
assert.match(backup, /requireComplete: true/, 'Pre-update backup must not silently skip files');
assert.match(backup, /GetSaveTargets\(def, requireAnyExisting: true\)/, 'Missing save folders must block update');
assert.match(orch, /_backups\.CreatePreUpdateAsync\(id, ct\)/, 'Game update must create a backup');
assert.match(orch, /if \(!preUpdate\.Success\)/, 'Failed backup must block update');

for (const code of ['GSC-INSTALL-STEAM', 'GSC-INSTALL-EXE', 'GSC-INSTALL-REGISTER',
  'GSC-INSTALL-ACCESS', 'GSC-INSTALL-PATH', 'GSC-INSTALL-IO']) {
  assert.ok(installer.includes(code), `Missing actionable installation error code: ${code}`);
}
for (const code of ['GSC-START-PROCESS', 'GSC-START-VM', 'GSC-START-GUEST']) {
  assert.ok(orch.includes(code), `Missing actionable launch error code: ${code}`);
}
console.log('Recovery, boot autostart, pre-update backup and diagnostics contracts OK.');
