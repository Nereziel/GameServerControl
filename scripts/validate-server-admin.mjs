import fs from 'node:fs';

const read = p => fs.readFileSync(p, 'utf8');
const required = (text, token, message) => {
  if (!text.includes(token)) throw new Error(`${message}: missing ${token}`);
};

const iface = read('src/GameServerControl.Agent/Rcon/IGameRcon.cs');
const service = read('src/GameServerControl.Agent/Rcon/RconService.cs');
const endpoints = read('src/GameServerControl.Agent/Servers/OperationalEndpoints.cs');
const ui = read('src/GameServerControl.Agent/WebUi/server-workspace.js');

required(iface, 'SupportedCommands', 'RCON handlers must advertise supported standard commands');
required(service, 'GetCapabilities', 'RCON service must expose handler capabilities');
required(endpoints, '/admin/capabilities', 'Agent must expose per-server admin capabilities');
required(ui, "['admin', 'Admin']", 'Workspace must expose an Admin tab for RCON servers');
required(ui, 'BroadcastMessage', 'Workspace must support advertised broadcast actions');
required(ui, 'Shutdown', 'Workspace must support advertised shutdown actions');
required(ui, 'RconStandardCommand.Raw', 'Raw commands must be explicitly filtered from advertised capabilities');

console.log('Server admin capability contract OK.');
