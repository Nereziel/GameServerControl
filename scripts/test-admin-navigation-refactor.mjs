import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const workspace=fs.readFileSync('src/GameServerControl.Agent/WebUi/server-workspace.js','utf8');
const styles=fs.readFileSync('src/GameServerControl.Agent/WebUi/styles.css','utf8');

const from=workspace.indexOf('function renderServerUpdates(def, body) {');
const to=workspace.indexOf('async function serverWorkspaceSaveSchedule(id)',from);
assert.ok(from>=0&&to>from,'Separate Updates and Schedule renderers must exist');
assert.equal(workspace.split('function renderServerSchedule(def, body)').length-1,1,
  'There must be one Schedule renderer, not a legacy override');
assert.equal(workspace.split('id="swUpdateTerminal"').length-1,1,
  'Only the Updates page may own the SteamCMD console');

let readonly=false;
let state='NotRunning';
let windows=true;
let polled=[];
const apiCalls=[];
const ctx={
  api:async path=>{
    apiCalls.push(path);
    return {dailyRestartEnabled:true,weeklyUpdateEnabled:true,hourlyBackupEnabled:true};
  },
  uiAction:(fn,args)=>'data-gsc-action="'+fn+'" data-args="'+JSON.stringify(args).replaceAll('"','&quot;')+'"',
  serverWorkspaceStatus:()=>({processState:state}),
  serverWorkspaceLooksWindows:()=>windows,
  serverWorkspaceStartUpdatePolling:id=>polled.push(id),
  profileFor:()=>({caps:{config:true}}),
  isReadOnly:()=>readonly,
  GSCUX:{actions:{}},
  esc:x=>String(x).replaceAll('&','&amp;').replaceAll('<','&lt;')
    .replaceAll('>','&gt;').replaceAll('"','&quot;'),
  console
};
vm.runInNewContext(workspace.slice(from,to),ctx);
const def={
  id:'dragon',name:'Dragonwilds',hostingMode:'BareMetal',steamAppId:'4019830',
  autoUpdateOnStart:true,autoUpdateOnRestart:false
};

const updates={innerHTML:''};
ctx.renderServerUpdates(def,updates);
assert.match(updates.innerHTML,/Game updates/);
assert.match(updates.innerHTML,/id="swManualUpdateButton"/);
assert.match(updates.innerHTML,/data-gsc-action="action"/);
assert.match(updates.innerHTML,/id="swUpdateState"/);
assert.match(updates.innerHTML,/id="swUpdateTerminal"/);
assert.match(updates.innerHTML,/id="swAutoUpdateOnStart"/);
assert.match(updates.innerHTML,/id="swAutoUpdateOnRestart"/);
assert.match(updates.innerHTML,/data-gsc-action="serverWorkspaceSaveAutoUpdate"/);
assert.match(updates.innerHTML,/Configure weekly schedule/);
assert.match(updates.innerHTML,/data-gsc-action="serverWorkspaceSetTab"/);
assert.deepEqual(polled,['dragon'],'the console should subscribe when Updates is shown');

const updateNow=()=>updates.innerHTML.match(/<button id="swManualUpdateButton"[^>]*>/)?.[0]||'';
assert.doesNotMatch(updateNow(),/\sdisabled(?:\s|=|>)/);
state='Running';
ctx.renderServerUpdates(def,updates);
assert.match(updateNow(),/\sdisabled(?:\s|=|>)/,'running game must not update');
state='NotRunning';
const noApp={...def,steamAppId:null};
ctx.renderServerUpdates(noApp,updates);
assert.match(updateNow(),/\sdisabled(?:\s|=|>)/,'non-Steam games cannot update');
readonly=true;
ctx.renderServerUpdates(def,updates);
assert.match(updateNow(),/\sdisabled(?:\s|=|>)/,'ReadOnly must not run updates');
assert.doesNotMatch(updates.innerHTML,/id="swUpdateTerminal"/,'ReadOnly cannot inspect SteamCMD console');
assert.match(updates.innerHTML,/Admin access is required/);
readonly=false;

const schedule={innerHTML:''};
await ctx.renderServerSchedule(def,schedule);
assert.match(schedule.innerHTML,/Scheduled maintenance/);
for(const id of ['swRestart','swRestartHour','swUpdate','swDay','swUpdateHour','swBackup','swBackupMinute'])
  assert.ok(schedule.innerHTML.includes('id="'+id+'"'),'scheduled control missing: '+id);
assert.doesNotMatch(schedule.innerHTML,/swUpdateTerminal|swUpdateState|swAutoUpdateOnStart/);
assert.doesNotMatch(schedule.innerHTML,/data-gsc-action="action"/,
  'Schedule must never directly run a game update or Apply configuration');
assert.match(schedule.innerHTML,/Open Updates/);
assert.match(schedule.innerHTML,/Windows autostart/);
assert.ok(apiCalls.every(path=>path==='/api/servers/dragon/schedule'),
  'Schedule must fetch only scheduled-task settings');
windows=false;
await ctx.renderServerSchedule(def,schedule);
assert.match(schedule.innerHTML,/systemd timers or cron/);
assert.match(schedule.innerHTML,/Save schedule<\/button>/);
assert.match(schedule.innerHTML,/button class="btn primary" type="button" disabled/);

assert.match(workspace,/const target='#server\/'\+encodeURIComponent\(id\)\+'\/updates'/,
  'manual and lifecycle updates must navigate to the Updates page');
assert.match(workspace,/SERVER_WORKSPACE\.tab!=='updates'/,
  'console polling must run only while the Updates page is active');
assert.match(workspace,/serverWorkspaceStopUpdatePolling\(\)/,
  'live polling must stop on navigation');
assert.match(workspace,/timer = setInterval\(/,
  'updates page should stream output even when a scheduled job starts it');
assert.match(workspace,/\(a==='restart'\|\|a==='apply'\)/,
  'Apply configuration must show an update console when restart auto-update is enabled');
assert.match(workspace,/Restart to apply/,'a restart belongs to Configuration, not Schedule');
assert.match(workspace,/Save configuration/);
assert.match(workspace,/checkbox\.defaultChecked = checkbox\.checked/,
  'successful preference save must reset unsaved-change detection');
assert.match(styles,/\.sw-page-intro\{/);
assert.match(styles,/\.sw-update-terminal\{/);

console.log('Task-based server administration, update isolation, Schedule-only jobs, ReadOnly, and polling contracts passed.');
