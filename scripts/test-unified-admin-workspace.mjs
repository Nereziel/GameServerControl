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

assert.ok(html.includes('id="tabSystem"')&&html.includes('id="systemView"'),
  'system management must have its own sidebar destination');
const top=html.match(/<div class="top-actions">([\s\S]*?)<\/div>/);
assert.ok(top,'header actions');
assert.ok(!/id="(?:tokensBtn|updateBtn|discoverBtn|addExistingBtn|manageHostsBtn)"/.test(top[1]),
  'secondary system/setup controls must leave the busy header');
for(const id of ['tokensBtn','updateBtn','manageHostsBtn','discoverBtn','addExistingBtn','createServerBtn'])
  assert.ok(html.includes('id="'+id+'"'),id+' action preserved');
assert.ok(core.includes("v==='system'")&&core.includes("renderSystemView()"),
  'system section must be a routable, functional page');
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
