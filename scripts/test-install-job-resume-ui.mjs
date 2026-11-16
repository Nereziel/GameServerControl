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
    if(path==='/api/servers/install/jobs')return [currentVersion];
    if(path==='/api/servers/install/queued123')return currentVersion;
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

console.log('Install history recovery, cancellation and legacy Agent fallback passed.');
