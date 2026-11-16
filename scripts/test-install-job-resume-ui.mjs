import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const script = fs.readFileSync('src/GameServerControl.Agent/WebUi/jobs-mods.js', 'utf8');
const split = script.indexOf('async function openMods(');
assert.ok(split > 0, 'Could not locate installation UI module');

const storage = new Map();
const requestPaths = [];
const job = {
  jobId:'queued123', phase:'download', line:'Restored after page refresh',
  percentHint:45, finished:false, success:false,
  serverName:'Fabric World', installPath:'/srv/gameservers/fabric'
};
let currentVersion = job;
let featureEnabled = true;
const badge = {textContent:'',classList:{toggle(){}}};
const jobs = {innerHTML:''};
const context = {
  CFG:{url:'https://example.invalid'},ACTIVE_HOST_ID:'local',JOB_CACHE:{},CURRENT_VIEW:'installs',
  localStorage:{
    getItem:key => storage.get(key)??null,
    setItem:(key,value)=>storage.set(key,value)
  },
  api: async(path,opts={})=>{
    requestPaths.push([path,opts.method||'GET']);
    if(path==='/api/servers/install/jobs')return Array.isArray(currentVersion)?currentVersion:[currentVersion];
    if(path.startsWith('/api/servers/install/')&&!path.endsWith('/cancel')){
      const id=path.split('/').at(-1);
      const records=Array.isArray(currentVersion)?currentVersion:[currentVersion];
      const found=records.find(p=>p.jobId===id);
      if(found)return found;
    }
    if(path==='/api/servers/install/queued123/cancel'&&opts.method==='POST')
      return {requested:true};
    throw new Error('Unexpected endpoint '+path);
  },
  supportsAgentFeature:key=>featureEnabled&&(key==='install.history'||key==='install.cancel'),
  isReadOnly:()=>false,
  $:sel=>sel==='#installBadge'?badge:sel==='#jobs'?jobs:null,
  uiAction:(name)=>'data-action="'+name+'"',
  uiActions:()=>'data-action="multi"',
  esc: x=>String(x??'').replaceAll('&','&amp;').replaceAll('<','&lt;'),
  confirm:()=>true,
  toast:()=>{}
};
vm.createContext(context);
vm.runInContext(script.slice(0,split),context);

await context.refreshJobs();
const records=context.loadJobRefs();
assert.equal(records.length,1,'Recovered jobs should appear without browser-local history');
assert.equal(records[0].jobId,'queued123');
assert.ok(jobs.innerHTML.includes('Fabric World'),'Recovered job must be shown');
assert.ok(jobs.innerHTML.includes('cancelInstallJob'),'Active supported jobs must be cancellable');
assert.equal(badge.textContent,1);

await context.cancelInstallJob('queued123');
assert.ok(requestPaths.some(([p,m])=>p==='/api/servers/install/queued123/cancel'&&m==='POST'),
  'Cancel must call the selected host API with POST');

featureEnabled=false;
requestPaths.length=0;
await context.refreshJobs();
assert.ok(!requestPaths.some(([p])=>p.endsWith('/jobs')),
  'Legacy agents should not be queried for unsupported install history');
assert.ok(!jobs.innerHTML.includes('cancelInstallJob'),
  'Legacy agents should never display a cancel control');


// A cleared completed install should stay dismissed even after Agent resync
// and browser reload; the durable Agent history itself must remain intact.
featureEnabled=true;
currentVersion={...job,phase:'done',line:'Installed',percentHint:100,finished:true,success:true};
vm.runInContext('INSTALL_HISTORY_POLL_AT[jobKey()] = 0',context);
await context.refreshJobs();
assert.equal(context.loadJobRefs().length,1);
context.clearFinishedJobs();
assert.equal(context.loadJobRefs().length,0,'Clear finished should remove the visible job');
vm.runInContext('INSTALL_HISTORY_POLL_AT[jobKey()] = 0',context);
await context.refreshJobs();
assert.equal(context.loadJobRefs().length,0,
  'Completed jobs explicitly cleared by the user must not be reimported by Agent history');

const reloaded={...context,JOB_CACHE:{}};
vm.createContext(reloaded);
vm.runInContext(script.slice(0,split),reloaded);
await reloaded.refreshJobs();
assert.equal(reloaded.loadJobRefs().length,0,
  'Explicitly cleared jobs must stay hidden after a browser reload');


// The Agent retains up to 100 jobs while the browser renders only 30.
// Clear finished must not leak the remaining records back into the UI.
storage.delete(context.jobKey());
storage.delete(context.dismissedJobKey());
context.JOB_CACHE={};
currentVersion=Array.from({length:35},(_,i)=>({...job,jobId:'complete'+i,
  phase:'done',line:'Installed',percentHint:100,finished:true,success:true}));
vm.runInContext('INSTALL_HISTORY_POLL_AT[jobKey()] = 0',context);
await context.refreshJobs();
assert.equal(context.loadJobRefs().length,30,'Browser should display at most 30 jobs');
context.clearFinishedJobs();
vm.runInContext('INSTALL_HISTORY_POLL_AT[jobKey()] = 0',context);
await context.refreshJobs();
assert.equal(context.loadJobRefs().length,0,
  'Clear finished must dismiss every terminal job returned by the Agent, not only visible cards');

console.log('Install recovery, cancel, dismissal, multi-page history and legacy fallback passed.');
