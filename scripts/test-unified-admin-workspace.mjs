import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const root='src/GameServerControl.Agent/WebUi/';
const workspace=fs.readFileSync(root+'server-workspace.js','utf8');
const html=fs.readFileSync(root+'index.html','utf8');
const core=fs.readFileSync(root+'core.js','utf8');
const admin=fs.readFileSync(root+'server-admin.js','utf8');
const styles=fs.readFileSync(root+'styles.css','utf8');

// Evaluate the real information architecture instead of asserting a visual mock-up.
const start=workspace.indexOf('function serverWorkspaceSections(');
const end=workspace.indexOf('function renderAllHostDashboard(',start);
assert.ok(start>=0&&end>start,'Workspace must declare navigable server sections');
const ctx={
  profileFor: def=>({caps:def.caps||{}}),
  serverWorkspaceTabs: def=>def.tabs
};
vm.runInNewContext(workspace.slice(start,end),ctx);
const complete={tabs:[
  ['console','Console'],['overview','Overview'],['diagnostics','Diagnostics'],
  ['config','Configuration'],['players','Players'],['admin','Admin'],
  ['mods','Mods'],['files','Files'],['backups','Backups'],
  ['network','Network'],['schedule','Schedule'],['settings','Settings']
],caps:{rcon:true}};
const sections=ctx.serverWorkspaceSections(complete);
assert.deepEqual(Array.from(sections,x=>x.key),
  ['overview','console','configuration','content','maintenance','diagnostics'],
  'six coherent top-level server sections');
const inner=Object.fromEntries(sections.map(s=>[s.key,Array.from(s.tabs,x=>x[0])]));
assert.deepEqual(inner.configuration,['config','access','network','settings'],
  'game-specific settings, RCON, network and advanced configuration stay together');
assert.deepEqual(inner.content,['files','mods'],'file and mod tools must be grouped');
assert.deepEqual(inner.maintenance,['backups','schedule'],'backup and automation stay together');
assert.deepEqual(inner.console,['console','players','admin'],'player/admin actions stay with console');
for(const tab of complete.tabs.map(x=>x[0])){
  assert.equal(ctx.serverWorkspaceResolveTab(complete,tab),tab,'old deep link must work: '+tab);
}
assert.equal(ctx.serverWorkspaceResolveTab(complete,'configuration'),'config');
assert.equal(ctx.serverWorkspaceResolveTab(complete,'content'),'files');
assert.equal(ctx.serverWorkspaceResolveTab(complete,'maintenance'),'backups');
assert.equal(ctx.serverWorkspaceSectionFor(complete,'network').key,'configuration');
assert.equal(ctx.serverWorkspaceSectionFor(complete,'mods').key,'content');
const bare={tabs:[['overview','Overview'],['diagnostics','Diagnostics'],['files','Files'],['backups','Backups'],['network','Network'],['schedule','Schedule'],['settings','Settings']],caps:{}};
assert.deepEqual(Array.from(ctx.serverWorkspaceSections(bare),x=>x.key),
  ['overview','configuration','content','maintenance','diagnostics'],
  'unsupported features must not create empty sections');
assert.equal(ctx.serverWorkspaceResolveTab(bare,'configuration'),'network');
assert.equal(ctx.serverWorkspaceResolveTab(bare,'invalid'),'overview');

// Verify that a form with untouched defaults navigates freely, while an edited
// field or a file-editor draft requests confirmation before leaving.
let controls=[];
let prompts=0;
let allowDiscard=false;
ctx.window={GSC_FILES:{dirty:false,content:null}};
ctx.SERVER_WORKSPACE={tab:'config'};
ctx.document={getElementById:id=>id==='swTabBody'?{querySelectorAll:()=>controls}:null};
ctx.confirm=()=>{prompts++;return allowDiscard};
assert.equal(ctx.serverWorkspaceHasUnsavedChanges(),false);
assert.equal(ctx.serverWorkspaceConfirmLeave(),true);
assert.equal(prompts,0,'untouched configuration must not prompt');
controls=[{type:'text',tagName:'INPUT',value:'changed',defaultValue:'initial',disabled:false}];
assert.equal(ctx.serverWorkspaceHasUnsavedChanges(),true);
assert.equal(ctx.serverWorkspaceConfirmLeave(),false,'Cancel must keep unsaved inputs');
assert.equal(prompts,1);
allowDiscard=true;
assert.equal(ctx.serverWorkspaceConfirmLeave(),true,'Confirm allows navigation');
controls=[{type:'select-one',tagName:'SELECT',disabled:false,multiple:false,selectedIndex:0,
  options:[{selected:true,defaultSelected:false},{selected:false,defaultSelected:false}]}];
assert.equal(ctx.serverWorkspaceHasUnsavedChanges(),false,
  'implicit first option selection is the browser default, not an edit');
controls[0].selectedIndex=1;
controls[0].options[0].selected=false;
controls[0].options[1].selected=true;
assert.equal(ctx.serverWorkspaceHasUnsavedChanges(),true,'real changed selection must prompt');
controls=[];
ctx.SERVER_WORKSPACE.tab='files';
ctx.window.GSC_FILES={dirty:true,content:{content:'unsaved fixture'}};
assert.equal(ctx.serverWorkspaceHasUnsavedChanges(),true,'file-editor drafts must be guarded');
assert.equal(ctx.serverWorkspaceConfirmLeave(),true);
assert.equal(ctx.window.GSC_FILES.dirty,false,'discard confirmation must reset file draft state');
assert.equal(ctx.window.GSC_FILES.content,null);

assert.ok(html.includes('id="tabSystem"')&&html.includes('id="systemView"')&&html.includes('id="settingsRoot"'),
  'all global Agent settings must share one sidebar destination');
assert.ok(!html.includes('id="tabSettings"')&&!html.includes('id="settingsView"'),
  'duplicate global Settings destination must be removed');
assert.match(html, /id="tabSystem"[^\n]*<span>Settings<\/span>/, 'global navigation should be named Settings');
const top=html.match(/<div class="top-actions">([\s\S]*?)<\/div>/);
assert.ok(top,'header actions');
assert.ok(!/id="(?:tokensBtn|updateBtn|discoverBtn|addExistingBtn|manageHostsBtn)"/.test(top[1]),
  'secondary system/setup controls must leave the busy header');
for(const id of ['tokensBtn','updateBtn','manageHostsBtn','discoverBtn','addExistingBtn','createServerBtn'])
  assert.ok(html.includes('id="'+id+'"'),id+' action preserved');
assert.ok(core.includes("v==='system'")&&core.includes("renderSystemView()")&&core.includes("renderGlobalSettings()"),
  'global Settings must be a routable page that includes Agent Relay settings');
assert.ok(core.includes('openAddServerMenu')&&core.includes('openAddExisting')&&core.includes('openDiscover'),
  'add server must offer install, existing and discovery without duplicate top-level buttons');
assert.ok(admin.includes("openServerPage(id,'overview')"),
  'legacy Manage action must open canonical workspace, not another dashboard');
assert.ok(!admin.includes("openModal('Manage — '"),
  'legacy duplicate Manage modal must be removed');
assert.ok(workspace.includes('serverWorkspaceHasUnsavedChanges'),
  'switching sections must check for unsaved form changes');
for(const cls of ['.sw-subtabs','.system-grid','.system-card'])
  assert.ok(styles.includes(cls),'missing responsive workspace style '+cls);
console.log('Unified server navigation, old deep links and system navigation contracts passed.');
