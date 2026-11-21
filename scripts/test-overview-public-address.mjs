import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js','utf8');
const start=source.indexOf('function serverWorkspaceConnectionDetails(');
const end=source.indexOf('function renderServerOverview(',start);
assert.ok(start>=0&&end>start,'Overview connection helper is present');
const esc=x=>String(x).replaceAll('&','&amp;').replaceAll('<','&lt;')
  .replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
const ctx={window:{},esc,uiAction:(action,args=[])=>'data-action="'+action+'" data-args="'+esc(JSON.stringify(args))+'"'};
vm.runInNewContext(source.slice(start,end),ctx);

const disabled=ctx.serverWorkspaceRenderConnectionDetails({
  enabled:false,configured:true,publicAddress:'203.0.113.45',targetAddress:'100.100.10.10'
});
assert.match(disabled,/No public VPS Relay route enabled/);
assert.doesNotMatch(disabled,/203\.0\.113\.45|100\.100\.10\.10/);

const missing=ctx.serverWorkspaceRenderConnectionDetails({
  enabled:true,configured:true,publicAddress:null,targetAddress:'100.100.10.10'
});
assert.match(missing,/Public IP \/ hostname is not configured/);
assert.doesNotMatch(missing,/100\.100\.10\.10/);

const actual={
  enabled:true,configured:true,reachable:true,publicAddress:'203.0.113.45',
  targetAddress:'100.100.10.10',ports:[
    {name:'Beacon',protocol:'Udp',publicPort:8888,publicEndPort:null,targetPort:8888},
    {name:'Game',protocol:'Udp',publicPort:7007,publicEndPort:null,targetPort:7777}
  ]
};
const markup=ctx.serverWorkspaceRenderConnectionDetails(actual);
assert.match(markup,/203\.0\.113\.45:7007/);
assert.match(markup,/203\.0\.113\.45:8888/);
assert.ok(markup.indexOf('203.0.113.45:7007')<markup.indexOf('203.0.113.45:8888'),
  'gameplay address should be listed before the beacon/query ports');
assert.match(markup,/Private\/Tailscale target: 100\.100\.10\.10 \(not public\)/);
assert.match(markup,/game port reachability is not verified/);
assert.match(markup,/serverWorkspaceCopyPublicAddress/);
assert.deepEqual(Array.from(ctx.serverWorkspaceConnectionDetails(actual).ports,port=>port.start),[7007,8888],
  'mapped Relay public ports, not local target ports, are displayed');

const ipv6=ctx.serverWorkspaceRenderConnectionDetails({
  enabled:true,configured:true,reachable:false,publicAddress:'2001:db8::42',
  ports:[{name:'Game',protocol:'UDP',publicPort:7777}]
});
assert.match(ipv6,/\[2001:db8::42\]:7777/);

const escaped=ctx.serverWorkspaceRenderConnectionDetails({
  enabled:true,configured:true,publicAddress:'<script>alert(1)</script>',
  ports:[{name:'<img onerror=alert(1)>',protocol:'UDP',publicPort:7777}]
});
assert.doesNotMatch(escaped,/<script>|<img/);
assert.match(escaped,/&lt;script&gt;/);

assert.match(source,/id="swPublicConnection"/);
assert.match(source,/void serverWorkspaceRefreshPublicAddress\(def\.id\)/);
assert.match(source,/\/api\/servers\/\$\{encodeURIComponent\(id\)\}\/relay/);
console.log('Overview public VPS Relay address, port mapping and fallback behavior passed.');
