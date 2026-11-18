import assert from 'node:assert/strict';
import fs from 'node:fs';

const core=fs.readFileSync('src/GameServerControl.Agent/WebUi/core.js','utf8');
const workspace=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js','utf8');
const boot=core.slice(core.indexOf('boot=async function(){'));
assert.ok(boot.includes('startStatusStream();'), 'Active WebUI boot must start the stream.');
assert.match(boot, /statusStreamReady && Date\.now\(\)-GSCUX\.lastHttpSync<60000/, 'Only full HTTP resync every minute while connected.');
assert.match(core, /stopStatusStream\(\);\s*ACTIVE_HOST_ID=id;/, 'Host changes must close old stream before changing identity.');
assert.ok(core.includes("message.target||''"), 'Stream must filter SignalR event target.');
assert.ok(core.includes("host!==ACTIVE_HOST_ID"), 'Stale host messages must be ignored.');
assert.ok(core.includes("GSCUX.statusStreamRetry=setTimeout(connect,5000)"), 'Reconnect after disconnect.');
assert.ok(core.includes('GSCUX.lastHttpSync=Date.now()'), 'Record successful HTTP refresh for fallback.');
assert.ok(core.includes("if(CURRENT_VIEW==='servers')renderServers();"), 'Status updates must refresh the visible server view.');
assert.ok(workspace.includes('async function createServerStatusConnection(onMessage, onClosed)'), 'Shared connection must expose close lifecycle.');
assert.ok(workspace.includes('ws.onclose = () =>'), 'Socket disconnect must clear heartbeat and pending RPC.');
console.log('SignalR-first status refresh contract OK.');
