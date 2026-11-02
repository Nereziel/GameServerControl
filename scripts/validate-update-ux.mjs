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
