import fs from 'node:fs';

const read = p => fs.readFileSync(p, 'utf8');
const required = (text, token, message) => {
  if (!text.includes(token)) throw new Error(`${message}: missing ${token}`);
};

const endpoints = read('src/GameServerControl.Agent/Servers/OperationalEndpoints.cs');
const updater = read('src/GameServerControl.Agent/Notifications/AgentUpdateService.cs');
const ui = read('src/GameServerControl.Agent/WebUi/core.js') + '\n' + read('src/GameServerControl.Agent/WebUi/server-admin.js');
const windowsInstaller = read('deploy/windows/install.ps1');
const linuxInstaller = read('deploy/linux/install-from-release.sh');

required(endpoints, '/update/preflight', 'Agent must expose update preflight');
required(updater, 'GetPreflight', 'Update service must calculate readiness');
required(ui, 'openUpdate', 'Web UI must expose the update workflow');
required(ui, '/api/update/preflight', 'Update modal must read update readiness');
required(ui, 'Update readiness', 'Update modal must show update readiness');
required(ui, 'startSelfUpdate', 'Web UI must be able to start the Agent updater');
required(ui, 'waitForAgentRestart', 'Web UI must reconnect after an update');
required(windowsInstaller, 'Rollback previous Agent payload', 'Windows installer must contain rollback path');
required(windowsInstaller, 'Agent.previous', 'Windows installer must retain the previous payload until startup succeeds');
required(linuxInstaller, 'Rollback previous Agent payload', 'Linux installer must contain rollback path');
required(linuxInstaller, '.previous', 'Linux installer must retain the previous payload until startup succeeds');

console.log('Update/install UX contract OK.');

const tray = read('src/GameServerControl.Tray/Program.cs');
required(tray, 'ReadInstalledAgentVersion(_installRoot', 'Tray must compare releases with the installed Agent executable');
required(tray, '_updateActionItem = new ToolStripMenuItem("Check for updates"', 'Tray must have one contextual update action');
required(tray, 'if (_updateAvailable) await StartUpdateAsync();', 'Tray update action must install only when a newer release is known');
required(tray, 'else await CheckForUpdatesAsync(showNoUpdate: true);', 'Tray update action must check when no newer release is known');
required(tray, '_updateAvailable = available;', 'Tray must update menu state after a version check');
required(tray, '_updateActionItem.Text = available ? $"Update Agent to {tag}…" : "Check for updates";',
  'Tray action text must reflect availability');
if (tray.includes('_checkUpdateItem') || tray.includes('_updateItem'))
  throw new Error('Tray must not reintroduce separate Check/Update menu entries.');
const trayMenu = tray.match(/menu\.Items\.AddRange\(\[([\s\S]*?)\]\);/)?.[1] ?? '';
if ((trayMenu.match(/_updateActionItem/g) || []).length !== 1)
  throw new Error('Tray context menu must contain exactly one update action.');
required(tray, 'FetchLatestReleaseAsync()', 'Tray must refresh GitHub release before launching');
required(tray, 'if (latest <= installed)', 'Tray must skip reinstallation and downgrades');
required(tray, 'psi.ArgumentList.Add("-Tag");', 'Tray must explicitly pass selected release to updater');
required(tray, 'psi.ArgumentList.Add(tag);', 'Tray must pin the resolved release tag');
required(windowsInstaller, 'if ($requestedTag) { $flags += \'-Tag\'; $flags += $requestedTag }',
  'Windows updater must preserve the explicit tag across UAC elevation');
