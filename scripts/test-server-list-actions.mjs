import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const work=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js','utf8');
const styles=fs.readFileSync('src/GameServerControl.Agent/WebUi/styles.css','utf8');
const gateway=fs.readFileSync('src/GameServerControl.Agent/MultiAgent/ManagedHostGateway.cs','utf8');
const admin=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-admin.js','utf8');
const start=work.indexOf('function serverWorkspaceRenderListActions(');
const end=work.indexOf('function renderAllHostDashboard(',start);
assert.ok(start>=0&&end>start, 'The new shared horizontal list renderer exists');
const esc=x=>String(x??'').replaceAll('&','&amp;').replaceAll('<','&lt;')
  .replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
let readOnly=false;
const calls=[];
const context={
  window:{},
  GSCUX:{actions:{}},
  ACTIVE_HOST_ID:'all',
  SERVERS:[],
  esc,
  isReadOnly:()=>readOnly,
  serverMeta:d=>({icon:'▣',label:'Dragonwilds'}),
  serverWorkspaceStateClass:s=>s==='Running'?'up':'down',
  uiAction:(name,args)=>`data-gsc-action="${esc(name)}" data-gsc-args="${esc(JSON.stringify(args))}"`,
  selectHost:async id=>{calls.push(['selectHost',id]);context.ACTIVE_HOST_ID=id;context.SERVERS=[{id:'dw'}]},
  action:async(id,op)=>calls.push(['action',id,op]),
  toast:message=>calls.push(['toast',message])
};
vm.runInNewContext(work.slice(start,end),context);
const def={id:'dw',name:'Dragonwilds',gameType:'SteamGeneric',hostingMode:'BareMetal',steamAppId:'4019830'};
const status={processState:'NotRunning',cpuPercent:0,memoryMB:512,pidInGuest:null,lastError:''};
const button=(html,label)=>{
  const all=html.match(/<button\b[^>]*>/g)||[];
  return all.find(b=>b.includes(`aria-label="${label} Dragonwilds"`))||'';
};
const stopped=context.serverWorkspaceRenderListRow(def,status);
assert.match(stopped,/sw-server-list-row/);
assert.match(stopped,/sw-list-name/);
assert.match(stopped,/Dragonwilds/);
assert.match(stopped,/CPU/);
assert.match(stopped,/RAM/);
assert.match(stopped,/PID/);
assert.match(stopped,/data-gsc-action="openServerPage"/);
assert.match(button(stopped,'Start'),/data-gsc-action="action"/);
assert.doesNotMatch(button(stopped,'Start'),/\sdisabled(?:\s|=|>)/);
assert.match(button(stopped,'Stop'),/\sdisabled/);
assert.match(button(stopped,'Restart'),/\sdisabled/);
assert.doesNotMatch(button(stopped,'Update'),/\sdisabled/);
assert.match(button(stopped,'Manage'),/data-gsc-action="openServerPage"/);
let depth=0;
for(const tag of stopped.match(/<\/?button\b[^>]*>/g)||[]){
  depth+=tag.startsWith('</')?-1:1;
  assert.ok(depth===0||depth===1,'The row must not nest interactive buttons');
}
assert.equal(depth,0,'Every quick-action button must be closed');

const running=context.serverWorkspaceRenderListRow(def,{...status,processState:'Running',pidInGuest:5678});
assert.match(button(running,'Start'),/\sdisabled/);
assert.doesNotMatch(button(running,'Stop'),/\sdisabled/);
assert.doesNotMatch(button(running,'Restart'),/\sdisabled/);
assert.match(button(running,'Update'),/\sdisabled/);
assert.match(running,/5678/);

const custom=context.serverWorkspaceRenderListRow({...def,steamAppId:null},status);
assert.match(button(custom,'Update'),/\sdisabled/);
assert.match(button(custom,'Update'),/No Steam App ID configured/);
context.GSCUX.actions.dw={state:'Starting'};
const busy=context.serverWorkspaceRenderListRow(def,status);
assert.match(button(busy,'Start'),/\sdisabled/);
assert.match(button(busy,'Update'),/\sdisabled/);
delete context.GSCUX.actions.dw;

readOnly=true;
const noActions=context.serverWorkspaceRenderListRow(def,status);
for(const name of ['Start','Stop','Restart','Update'])
  assert.match(button(noActions,name),/\sdisabled/, 'ReadOnly must not act: '+name);
readOnly=false;

const remote=context.serverWorkspaceRenderListRow(def,status,{hostId:'host-one',hostName:'Remote computer'});
assert.match(button(remote,'Update'),/serverWorkspaceHostAction/);
assert.match(remote,/Remote computer/);
assert.match(button(remote,'Manage'),/selectHostAndOpen/);
await context.serverWorkspaceHostAction('host-one','dw','update');
assert.deepEqual(calls,[['selectHost','host-one'],['action','dw','update']],
  'An All computers action must select the exact host before invoking the server operation');
calls.length=0;
readOnly=true;
await context.serverWorkspaceHostAction('host-one','dw','start');
assert.deepEqual(calls,[], 'ReadOnly must not execute multi-host actions');

assert.match(work,/class="sw-server-list" role="list"/);
assert.match(work,/serverWorkspaceRenderListRow\(def, serverWorkspaceStatus\(def.id\)\)/);
assert.match(work,/serverWorkspaceRenderListRow\(server, server, \{hostId: host.id/);
assert.match(styles,/\.sw-server-list-row\{display:grid;/);
assert.match(styles,/@media\(max-width:760px\)/);
assert.match(styles,/\.sw-list-actions/);
assert.match(gateway,/string\? SteamAppId = null/);
assert.match(gateway,/ReadString\(def, "steamAppId"\)/);
assert.match(admin,/serverWorkspace\(\?:HostAction\|/);

console.log('Full-width server list, action availability, ReadOnly controls and multi-host routing passed.');
