import assert from 'node:assert/strict';
import fs from 'node:fs';

const core=fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js','utf8');
const workspace=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js','utf8');
const program=fs.readFileSync('src/GameServerControl.Agent/Program.cs','utf8');
const server=fs.readFileSync('src/GameServerControl.Agent/Servers/ServerOrchestrator.cs','utf8');

assert.ok(core.includes("uiAction('action',[d.id,'update'])"), 'Update must be visible beside Start / Stop / Restart');
assert.ok(workspace.includes('id="swUpdateButton"'), 'Workspace header needs an Update button');
assert.ok(workspace.includes('update.disabled = running || busy || !def.steamAppId'), 'Cannot update a running game');
assert.ok(workspace.includes('id="swAutoUpdateOnStart"'), 'Auto-update on Start toggle missing');
assert.ok(workspace.includes('id="swAutoUpdateOnRestart"'), 'Auto-update on Restart toggle missing');
assert.ok(workspace.includes("uiAction('serverWorkspaceSaveAutoUpdate'"), 'Per-server auto-update preferences must be saved');
assert.ok(workspace.includes('/auto-update`'), 'Preferences must use a dedicated, narrow endpoint');
assert.ok(workspace.includes('id="swUpdateTerminal"'), 'Update console must be visible in Backups & automation');
assert.ok(workspace.includes('/update/console?since='), 'Update console should stream incremental, bounded output');
assert.ok(workspace.includes("a==='start'&&!!def?.autoUpdateOnStart"), 'Auto-start must also show update progress');
assert.ok(workspace.includes("a==='restart'&&!!def?.autoUpdateOnRestart"), 'Auto-restart must also show update progress');
assert.ok(program.includes('ServerUpdateConsole updates'), 'Update console must be served by Agent');
assert.ok(program.includes('.RequireAuthorization("admin")'), 'Update console output must require an Admin');
assert.ok(server.includes('WithLifecycleLockAsync'), 'Server lifecycle must serialize update/start/stop');
assert.ok(server.includes('Stop the game server before updating it'), 'SteamCMD must refuse running server updates');
console.log('Auto-update preferences, visible Update action and progress console contract OK.');
