import fs from 'node:fs';

const read = p => fs.readFileSync(p, 'utf8');
const required = (text, token, message) => {
  if (!text.includes(token)) throw new Error(`${message}: missing ${token}`);
};

const program = read('src/GameServerControl.Agent/Program.cs');
const updater = read('src/GameServerControl.Agent/Notifications/AgentUpdateService.cs');
const ui = read('src/GameServerControl.Agent/WebUi/jobs-mods.js');
const windowsInstaller = read('deploy/windows/install.ps1');
const linuxInstaller = read('deploy/linux/install-from-release.sh');

required(program, '/update/preflight', 'Agent must expose update preflight');
required(updater, 'GetPreflight', 'Update service must calculate readiness');
required(ui, 'Update readiness', 'Global Settings must show update readiness');
required(ui, '/api/update/preflight', 'Global Settings must call update preflight');
required(windowsInstaller, 'Rollback previous Agent payload', 'Windows installer must contain rollback path');
required(linuxInstaller, 'Rollback previous Agent payload', 'Linux installer must contain rollback path');

console.log('Update/install UX contract OK.');
