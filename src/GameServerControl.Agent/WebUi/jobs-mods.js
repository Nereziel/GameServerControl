function jobKey(){return'gsc.install.jobs.'+CFG.url+'.'+ACTIVE_HOST_ID}
function loadJobRefs(){try{return JSON.parse(localStorage.getItem(jobKey())||'[]')}catch{return[]}}
function saveJobRefs(v){localStorage.setItem(jobKey(),JSON.stringify(v.slice(0,30)))}
function rememberJob(m){const a=loadJobRefs().filter(x=>x.jobId!==m.jobId);a.unshift(m);saveJobRefs(a)}
function dismissedJobKey(){return jobKey()+'.dismissed'}
function loadDismissedJobIds(){
  try{
    const saved=JSON.parse(localStorage.getItem(dismissedJobKey())||'[]');
    return new Set(Array.isArray(saved)?saved.filter(id=>typeof id==='string'&&id.length>0&&id.length<=64):[]);
  }catch{return new Set()}
}
function dismissJobIds(ids){
  const dismissed=loadDismissedJobIds();
  for(const id of ids)if(typeof id==='string'&&id.length>0&&id.length<=64)dismissed.add(id);
  // The Agent keeps at most 100 install summaries; cap the browser tombstones as well.
  localStorage.setItem(dismissedJobKey(),JSON.stringify([...dismissed].slice(-100)));
}

const INSTALL_HISTORY_POLL_AT={};
async function recoverServerInstallJobs(){
  if(ACTIVE_HOST_ID==='all'||!supportsAgentFeature('install.history')||
      (typeof isReadOnly==='function'&&isReadOnly()))return;
  const key=jobKey(), now=Date.now();
  if(now-(INSTALL_HISTORY_POLL_AT[key]||0)<30000)return;
  INSTALL_HISTORY_POLL_AT[key]=now;
  try{
    const records=await api('/api/servers/install/jobs');
    if(!Array.isArray(records))return;
    const existing=loadJobRefs();
    const dismissed=loadDismissedJobIds();
    const known=new Set(existing.map(x=>x.jobId));
    const recovered=[];
    for(const p of records){
      if(!p||typeof p.jobId!=='string'||p.jobId.length>64)continue;
      if(p.finished&&dismissed.has(p.jobId))continue;
      JOB_CACHE[p.jobId]=p;
      if(!known.has(p.jobId)){
        known.add(p.jobId);
        recovered.push({jobId:p.jobId,name:p.serverName||p.gameKey||'Install job',
          icon:'⬇',path:p.installPath||''});
      }
    }
    if(recovered.length)saveJobRefs([...recovered,...existing]);
  }catch{
    // The selected host may be offline. Existing browser history still works.
  }
}

async function refreshJobs(render=true){
  if(ACTIVE_HOST_ID==='all'){if(render||CURRENT_VIEW==='installs')renderJobs();return}
  await recoverServerInstallJobs();
  const refs=loadJobRefs();let active=0;
  for(const r of refs){
    try{
      JOB_CACHE[r.jobId]=await api('/api/servers/install/'+encodeURIComponent(r.jobId));
      if(!JOB_CACHE[r.jobId].finished)active++;
    }catch{
      JOB_CACHE[r.jobId]={jobId:r.jobId,phase:'expired',
        line:'Job history unavailable on this Agent.',finished:true,success:false,logLines:[]};
    }
  }
  const badge=$('#installBadge');
  if(badge){badge.textContent=active;badge.classList.toggle('hidden',active===0)}
  if(render||CURRENT_VIEW==='installs')renderJobs();
}
function phaseText(p){return({queued:'Queued',steamcmd:'SteamCMD',download:'Downloading',verify:'Verifying',
  register:'Registering',done:'Completed',failed:'Failed',cancelled:'Cancelled',
  interrupted:'Interrupted by Agent restart',expired:'Unavailable'})[p]||p}
function mayCancelInstall(p){
  return !!p&&!p.finished&&supportsAgentFeature('install.cancel')&&
    !(typeof isReadOnly==='function'&&isReadOnly());
}
function renderJobs(){
  const refs=loadJobRefs(),root=$('#jobs');
  if(!root)return;
  if(ACTIVE_HOST_ID==='all'){
    root.innerHTML='<div class="empty">Select a computer from the sidebar to see its installation jobs.</div>';return;
  }
  if(!refs.length){
    root.innerHTML='<div class="empty">No installation history on this computer.</div>';return;
  }
  root.innerHTML=refs.map(r=>{
    const p=JOB_CACHE[r.jobId]||{},pct=p.percentHint??0,
      cls=p.finished?(p.success?'finished':'failed'):'',
      state=p.finished?(p.success?'DONE':p.phase==='cancelled'?'CANCELLED':p.phase==='interrupted'?'INTERRUPTED':'FAILED'):'ACTIVE',
      cancel=mayCancelInstall(p)
        ?`<button class="btn danger" ${uiAction('cancelInstallJob',[r.jobId])}>Cancel install</button>`:'';
    return `<div class="job ${cls}"><div class="jobtop"><div style="font-size:24px">${r.icon||'⬇'}</div><div class="grow"><div class="jobname">${esc(r.name||r.jobId)}</div><div class="jobphase">${esc(phaseText(p.phase||'loading'))} · ${esc(r.path||'')} · ${esc(r.jobId)}</div></div><div class="state ${p.success?'up':p.finished?'down':'warn'}">${state}</div></div><div class="progress"><div class="bar" style="width:${Math.max(0,Math.min(100,pct))}%"></div></div><div class="jobline">${esc(p.line||'Loading…')}</div><div class="jobbuttons"><button class="btn" ${uiAction('openJob',[r.jobId])}>Details & log</button>${cancel}${p.success?`<button class="btn" ${uiActions([['showView',['servers']],['loadServers',[]]])}>Open servers</button>`:''}</div></div>`;
  }).join('');
}
async function cancelInstallJob(id){
  if(!confirm('Cancel this installation? Any completed game files will be left in place.'))return;
  try{
    await api('/api/servers/install/'+encodeURIComponent(id)+'/cancel',{method:'POST'});
    toast('Cancellation requested.','warn');
    await refreshJobs();
  }catch(e){toast('Could not cancel installation: '+e.message,'err')}
}
function clearFinishedJobs(){
  const refs=loadJobRefs();
  const finished=refs.filter(r=>JOB_CACHE[r.jobId]?.finished).map(r=>r.jobId);
  dismissJobIds(finished);
  saveJobRefs(refs.filter(r=>!finished.includes(r.jobId)));
  renderJobs();
  refreshJobs(false);
}
async function openJob(id){
  await refreshJobs(false);
  const r=loadJobRefs().find(x=>x.jobId===id)||{jobId:id},p=JOB_CACHE[id]||{},
    log=(p.logLines||[]).join('\n'),
    cancel=mayCancelInstall(p)?`<button class="btn danger" ${uiAction('cancelInstallJob',[id])}>Cancel install</button>`:'';
  openModal(r.name||id,
    `<div class="jobphase">Job ${esc(id)} · ${esc(phaseText(p.phase||''))}</div><div class="progress"><div class="bar" style="width:${Math.max(0,Math.min(100,p.percentHint??0))}%"></div></div><div class="jobline" style="margin-bottom:10px">${esc(p.line||'')}</div><pre class="log">${esc(log||'No log lines yet.')}</pre>`,
    `<button class="btn" ${uiAction('openJob',[id])}>Refresh</button>${cancel}<button class="btn" ${uiAction('closeModal')}>Run in background</button>`,true);
}
async function openMods(id){const d=SERVERS.find(x=>x.id===id);MOD_CTX={id,def:d,tab:'installed',serverSideOnly:true,hideDeprecated:true,category:'',sort:'downloads'};openModal('Mods — '+(d?.name||id),'<div id="modsRoot">Loading…</div>','',true);await renderMods()}
async function renderMods(){const root=$('#modsRoot');if(!root)return;root.innerHTML=`<div class="subtabs"><button class="subtab ${MOD_CTX.tab==='installed'?'active':''}" ${uiAction('switchMods',['installed'])}>Installed</button><button class="subtab ${MOD_CTX.tab==='browse'?'active':''}" ${uiAction('switchMods',['browse'])}>Browse</button><button class="subtab ${MOD_CTX.tab==='updates'?'active':''}" ${uiAction('switchMods',['updates'])}>Updates</button></div><div id="modsPane"><div class="muted">Loading…</div></div>`;if(MOD_CTX.tab==='installed')await loadInstalledMods();else if(MOD_CTX.tab==='browse')await loadBrowseMods();else await loadModUpdates()}
async function switchMods(tab){MOD_CTX.tab=tab;await renderMods()}
function setModSort(value){MOD_CTX.sort=value;applyModFilters()}
async function setModServerOnly(checked){MOD_CTX.serverSideOnly=checked;await searchMods()}
function setModHideDeprecated(checked){MOD_CTX.hideDeprecated=checked;applyModFilters()}

let MOD_CONFIG_EDIT=null;
function safeExternalModUrl(url){try{const u=new URL(String(url||''));return u.protocol==='https:'||u.protocol==='http:'?u.toString():null}catch{return null}}
function openExternalModUrl(url){const safe=safeExternalModUrl(url);if(!safe)return toast('Could not open external link: only HTTP(S) URLs are allowed.','err');window.open(safe,'_blank','noopener,noreferrer')}
function modRestartNotice(){if((STATUS[MOD_CTX.id]?.processState||'')==='Running')toast('Server is running. Restart recommended to apply mod/plugin changes.','warn')}
function modConfigTokens(modId,displayName){return [...new Set(`${modId||''} ${displayName||''}`.toLowerCase().split(/[^a-z0-9]+/).filter(x=>x.length>=3))]}
function rankModConfigFiles(files,modId,displayName){const tokens=modConfigTokens(modId,displayName);return (files||[]).filter(f=>String(f.group||'')==='Mods / plugins').map(f=>{const hay=`${f.path||''} ${f.name||''}`.toLowerCase();return{file:f,score:tokens.reduce((n,t)=>n+(hay.includes(t)?1:0),0)}}).sort((a,b)=>b.score-a.score||String(a.file.path||'').localeCompare(String(b.file.path||'')))}
async function configureInstalledMod(modId,displayName){try{const r=await api(`/api/servers/${MOD_CTX.id}/files`);if(!r.supported)return openModal('Configure — '+displayName,`<div class="empty">${esc(r.reason||'Config-file editing is not available for this server.')}</div>`,`<button class="btn" ${uiAction('openMods',[MOD_CTX.id])}>Back to mods</button>`,true);const ranked=rankModConfigFiles(r.files||[],modId,displayName),likely=ranked.filter(x=>x.score>0),other=ranked.filter(x=>x.score===0);const rows=list=>list.map(x=>`<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(x.file.name||x.file.path)}</div><div class="sw-row-meta mono">${esc(x.file.path)} · ${esc(x.file.format||'text')}${x.file.restartRecommended?' · restart recommended':''}</div></div><button class="btn" ${uiAction('openModConfigFile',[x.file.path,modId,displayName])}>Edit</button></div>`).join('');const body=ranked.length?`${likely.length?`<div class="section">Likely config files</div>${rows(likely)}`:''}${other.length?`<div class="section">Other mod/plugin config files</div><div class="help">Shown as a fallback because some mods use config filenames unrelated to their package name.</div>${rows(other)}`:''}`:'<div class="empty">No allowed mod/plugin config files were found. Start the server/mod once if it creates its config on first launch.</div>';openModal('Configure — '+displayName,body,`<button class="btn" ${uiAction('openMods',[MOD_CTX.id])}>Back to mods</button>`,true)}catch(e){toast('Config discovery failed: '+e.message,'err')}}
async function openModConfigFile(path,modId,displayName){try{const[c,h]=await Promise.all([api(`/api/servers/${MOD_CTX.id}/files/content?path=${encodeURIComponent(path)}`),api(`/api/servers/${MOD_CTX.id}/files/history?path=${encodeURIComponent(path)}`).catch(()=>[])]);MOD_CONFIG_EDIT={path,modId,displayName,sha256:c.sha256};const history=(h||[]).map(x=>`<div class="sw-row"><div class="grow"><div class="sw-row-title">${new Date(x.createdAt).toLocaleString()}</div><div class="sw-row-meta">${Math.max(1,Math.round((x.sizeBytes||0)/1024))} KB</div></div><button class="btn" ${uiAction('restoreModConfigFile',[x.id])}>Restore</button></div>`).join('');openModal('Config — '+displayName,`<div class="jobphase mono">${esc(path)}</div><textarea id="modConfigContent" class="mono" style="width:100%;min-height:360px;margin-top:10px">${esc(c.content||'')}</textarea><div class="section">History</div>${history||'<div class="muted">No previous versions yet.</div>'}`,`<button class="btn" ${uiAction('configureInstalledMod',[modId,displayName])}>Back</button><button class="btn primary" ${uiAction('saveModConfigFile')}>Validate & save</button>`,true)}catch(e){toast('Config open failed: '+e.message,'err')}}
async function saveModConfigFile(){if(!MOD_CONFIG_EDIT)return;const content=$('#modConfigContent')?.value??'';try{await api(`/api/servers/${MOD_CTX.id}/files/validate`,{method:'POST',body:JSON.stringify({path:MOD_CONFIG_EDIT.path,content})});const r=await api(`/api/servers/${MOD_CTX.id}/files/content`,{method:'PUT',body:JSON.stringify({path:MOD_CONFIG_EDIT.path,content,expectedSha256:MOD_CONFIG_EDIT.sha256})});MOD_CONFIG_EDIT.sha256=r.sha256;toast('Mod/plugin config saved.','ok');modRestartNotice();await openModConfigFile(MOD_CONFIG_EDIT.path,MOD_CONFIG_EDIT.modId,MOD_CONFIG_EDIT.displayName)}catch(e){toast('Config save failed: '+e.message,'err')}}
async function restoreModConfigFile(backupId){if(!MOD_CONFIG_EDIT)return;if(!confirm('Restore this config version? The current version will be saved to history first.'))return;try{const r=await api(`/api/servers/${MOD_CTX.id}/files/restore`,{method:'POST',body:JSON.stringify({path:MOD_CONFIG_EDIT.path,backupId,expectedSha256:MOD_CONFIG_EDIT.sha256})});MOD_CONFIG_EDIT.sha256=r.sha256;toast('Config restored.','ok');modRestartNotice();await openModConfigFile(MOD_CONFIG_EDIT.path,MOD_CONFIG_EDIT.modId,MOD_CONFIG_EDIT.displayName)}catch(e){toast('Config restore failed: '+e.message,'err')}}

async function loadInstalledMods(){const pane=$('#modsPane');try{const[r,u]=await Promise.all([api(`/api/servers/${MOD_CTX.id}/mods`),api(`/api/servers/${MOD_CTX.id}/mods/updates`).catch(()=>({updates:[]}))]);if(!r.supported){pane.innerHTML=`<div class="empty">${esc(r.unsupportedReason||'Mod management is not available for this game.')}</div>`;return}MOD_UPDATES=u.updates||[];const updateMap=new Map(MOD_UPDATES.map(x=>[x.modId,x]));pane.innerHTML=`<div class="summarybar"><div class="muted tiny">${(r.mods||[]).length} installed · Folder: ${esc(r.modsFolder||'')}</div><div class="spacer"></div><button class="btn" ${uiAction('switchMods',['updates'])}>Check updates ${MOD_UPDATES.length?`(${MOD_UPDATES.length})`:''}</button></div>${r.mods?.length?`<div class="modlist">${r.mods.map(m=>{const up=updateMap.get(m.modId),source=safeExternalModUrl(m.source);return `<div class="modcard"><div class="modhead"><div class="grow"><div class="modname">${esc(m.displayName)}</div><div class="modmeta">${esc(m.modId)} · ${esc(m.version||'version unknown')}${m.sizeBytes?` · ${Math.max(1,Math.round(m.sizeBytes/1024/1024))} MB`:''}</div></div>${up?'<span class="state warn">UPDATE</span>':''}</div>${up?`<div class="updatebox">${esc(m.version||'?')} → <b>${esc(up.latestVersion)}</b></div>`:''}<div class="modactions">${up?`<button class="btn primary" ${uiAction('updateOneMod',[m.modId])}>Update</button>`:''}<button class="btn" ${uiAction('configureInstalledMod',[m.modId,m.displayName])}>Configure</button>${source?`<button class="btn" ${uiAction('openExternalModUrl',[source])}>Source</button>`:''}<button class="btn danger" ${uiAction('removeMod',[m.modId])}>Remove</button></div></div>`}).join('')}</div>`:'<div class="empty">No managed mods installed.</div>'}<div class="section">Install from URL</div><div class="toolbar"><input id="manualModUrl" placeholder="https://.../mod.zip"><input id="manualModName" placeholder="Display name (optional)"><button class="btn primary" ${uiAction('installManualMod')}>Install</button></div>`}catch(e){pane.innerHTML=`<div class="empty">${esc(e.message)}</div>`}}
async function loadBrowseMods(){const pane=$('#modsPane');pane.innerHTML=`<div class="toolbar"><input id="modQuery" placeholder="Search mods…" ${uiAction('searchMods',[],{event:'keydown',key:'Enter'})}><select id="modCategory" ${uiAction('applyModFilters',[],{event:'change'})}><option value="">All categories</option></select><select id="modSort" ${uiAction('setModSort',[],{event:'change',pass:'value'})}><option value="downloads">Most downloaded</option><option value="rating">Rating</option><option value="name">Name</option></select></div><div class="filters"><label><input id="modServerOnly" type="checkbox" ${MOD_CTX.serverSideOnly?'checked':''} ${uiAction('setModServerOnly',[],{event:'change',pass:'checked'})}> Server-side only</label><label><input id="modHideDep" type="checkbox" ${MOD_CTX.hideDeprecated?'checked':''} ${uiAction('setModHideDeprecated',[],{event:'change',pass:'checked'})}> Hide deprecated</label><span id="modSource" class="source"></span></div><div id="modResults"><div class="muted">Loading marketplace…</div></div>`;await searchMods()}
async function searchMods(){const q=$('#modQuery')?.value||'',out=$('#modResults');if(out)out.innerHTML='<div class="muted">Searching…</div>';try{const r=await api(`/api/servers/${MOD_CTX.id}/mods/search?q=${encodeURIComponent(q)}&limit=200&serverSideOnly=${MOD_CTX.serverSideOnly}`);if(!r.supported){out.innerHTML=`<div class="empty">${esc(r.unsupportedReason||'Marketplace search unavailable.')}</div>`;return}MOD_RESULTS=r.results||[];const source=$('#modSource');if(source)source.textContent=r.source?'Primary source: '+r.source:'';populateCategories();applyModFilters()}catch(e){out.innerHTML=`<div class="empty">${esc(e.message)}</div>`}}
function populateCategories(){const el=$('#modCategory');if(!el)return;const cur=MOD_CTX.category;const cats=[...new Set(MOD_RESULTS.flatMap(x=>x.categories||[]))].sort((a,b)=>a.localeCompare(b));el.innerHTML='<option value="">All categories</option>'+cats.map(c=>`<option value="${esc(c)}" ${c===cur?'selected':''}>${esc(c)}</option>`).join('')}
function applyModFilters(){const out=$('#modResults');if(!out)return;MOD_CTX.category=$('#modCategory')?.value||'';let list=MOD_RESULTS.filter(m=>(!MOD_CTX.hideDeprecated||!m.deprecated)&&(!MOD_CTX.category||(m.categories||[]).includes(MOD_CTX.category)));if(MOD_CTX.sort==='rating')list.sort((a,b)=>(b.ratingScore||0)-(a.ratingScore||0));else if(MOD_CTX.sort==='name')list.sort((a,b)=>(a.name||'').localeCompare(b.name||''));else list.sort((a,b)=>(b.downloads||0)-(a.downloads||0));if(!list.length){out.innerHTML='<div class="empty">No mods match these filters.</div>';return}out.innerHTML=`<div class="modlist">${list.map(m=>`<div class="modcard"><div class="modhead"><div class="grow"><div class="modname">${esc(m.name)}</div><div class="modmeta">by ${esc(m.owner||'?')} · v${esc(m.version||'?')} · ${(m.downloads||0).toLocaleString()} downloads</div></div>${m.deprecated?'<span class="state down">DEPRECATED</span>':''}</div><div class="moddesc">${esc(m.description||'No description.')}</div><div class="pills">${m.serverSideOnly?'<span class="pill ok">SERVER-SIDE</span>':'<span class="pill warn">CLIENT MAY BE REQUIRED</span>'}${m.deprecated?'<span class="pill bad">DEPRECATED</span>':''}${(m.categories||[]).slice(0,6).map(c=>`<span class="pill">${esc(c)}</span>`).join('')}</div><div class="modactions"><button class="btn primary" ${uiAction(m.deprecated?'installDeprecatedMod':'installSearchMod',[],{pass:'element'})} data-url="${esc(m.downloadUrl)}" data-name="${esc(m.name)}" ${!m.downloadUrl?'disabled':''}>Install</button>${m.downloadUrl?`<button class="btn" ${uiAction('openExternalModUrl',[m.downloadUrl])}>Download</button>`:''}${m.packageUrl?`<button class="btn" ${uiAction('openExternalModUrl',[m.packageUrl])}>Page</button>`:''}</div></div>`).join('')}</div>`}
function dataMod(btn){return{url:btn.dataset.url||'',name:btn.dataset.name||''}}
async function installSearchMod(btn){const m=dataMod(btn);await installModUrl(m.url,m.name)}
async function installDeprecatedMod(btn){const m=dataMod(btn);if(!confirm(`${m.name} is marked DEPRECATED by the marketplace. Install anyway?`))return;await installModUrl(m.url,m.name)}
async function installModUrl(url,name){if(!url)return toast('No download URL available.','err');try{const r=await api(`/api/servers/${MOD_CTX.id}/mods/install`,{method:'POST',body:JSON.stringify({url,displayName:name||null})});if(!r.ok)throw new Error(r.error||'Install failed');toast('Installed '+(r.mod?.displayName||name),'ok');modRestartNotice();MOD_CTX.tab='installed';await renderMods()}catch(e){toast('Mod install failed: '+e.message,'err')}}
async function installManualMod(){await installModUrl($('#manualModUrl').value.trim(),$('#manualModName').value.trim())}
async function removeMod(id){if(!confirm('Remove '+id+'?'))return;try{await api(`/api/servers/${MOD_CTX.id}/mods/${encodeURIComponent(id)}`,{method:'DELETE'});toast('Mod removed.','ok');modRestartNotice();await loadInstalledMods()}catch(e){toast('Remove failed: '+e.message,'err')}}
async function loadModUpdates(){const pane=$('#modsPane');try{const r=await api(`/api/servers/${MOD_CTX.id}/mods/updates`);if(!r.supported){pane.innerHTML=`<div class="empty">${esc(r.unsupportedReason||'Update checks unavailable.')}</div>`;return}MOD_UPDATES=r.updates||[];if(!MOD_UPDATES.length){pane.innerHTML='<div class="empty">All managed mods are up to date.</div>';return}pane.innerHTML=`<div class="summarybar"><b>${MOD_UPDATES.length} update${MOD_UPDATES.length===1?'':'s'} available</b><div class="spacer"></div><button class="btn primary" ${uiAction('updateAllMods')}>Update all</button></div><div class="modlist">${MOD_UPDATES.map(u=>`<div class="modcard"><div class="modname">${esc(u.displayName)}</div><div class="modmeta">${esc(u.installedVersion||'?')} → ${esc(u.latestVersion)}</div><div class="modactions"><button class="btn primary" ${uiAction('updateOneMod',[u.modId])}>Update</button>${u.latestDownloadUrl?`<button class="btn" ${uiAction('openExternalModUrl',[u.latestDownloadUrl])}>Download</button>`:''}</div></div>`).join('')}</div>`}catch(e){pane.innerHTML=`<div class="empty">${esc(e.message)}</div>`}}
async function updateOneMod(id){const u=MOD_UPDATES.find(x=>x.modId===id);if(!u)return;try{await api(`/api/servers/${MOD_CTX.id}/mods/${encodeURIComponent(id)}/update`,{method:'POST',body:JSON.stringify({url:u.latestDownloadUrl,displayName:u.displayName})});toast('Updated '+u.displayName,'ok');modRestartNotice();await loadModUpdates()}catch(e){toast('Update failed: '+e.message,'err')}}
async function updateAllMods(){if(!MOD_UPDATES.length)return;const failures=[];let updated=0;for(const u of [...MOD_UPDATES]){try{await api(`/api/servers/${MOD_CTX.id}/mods/${encodeURIComponent(u.modId)}/update`,{method:'POST',body:JSON.stringify({url:u.latestDownloadUrl,displayName:u.displayName})});updated++}catch(e){failures.push(`${u.displayName}: ${e.message}`)}}toast(`Update all finished: ${updated} updated, ${failures.length} failed${failures.length?' — '+failures.join('; '):''}`,failures.length?'err':'ok');if(updated)modRestartNotice();await loadModUpdates()}

/* Agent-wide Settings. Relay connection credentials belong to the Agent, not one game server. */
function installGlobalSettingsView(){
  const tabs=document.querySelector('.sidebar-nav');
  const installs=$('#installsView');
  if(tabs&&!$('#tabSettings'))tabs.insertAdjacentHTML('beforeend',`<button id="tabSettings" class="tab" type="button" ${uiAction('showView',['settings'])}><span class="nav-icon" aria-hidden="true">⚙</span><span>Settings</span></button>`);
  if(installs&&!$('#settingsView'))installs.insertAdjacentHTML('afterend','<section id="settingsView" class="hidden"><div id="settingsRoot"></div></section>');
}
const GLOBAL_SETTINGS_BASE_SHOW_VIEW=showView;
showView=function(v){
  installGlobalSettingsView();
  if(v==='settings'){
    CURRENT_VIEW='settings';
    setWorkspaceHeading('Settings','CONTROL PANEL / AGENT');
    closeMobileNavigation();
    $('#serversView')?.classList.add('hidden');
    $('#installsView')?.classList.add('hidden');
    $('#settingsView')?.classList.remove('hidden');
    $('#tabServers')?.classList.remove('active');
    $('#tabInstalls')?.classList.remove('active');
    $('#tabSettings')?.classList.add('active');
    if(ACTIVE_HOST_ID==='all'){
      const root=$('#settingsRoot');if(root)root.innerHTML='<div class="empty">Select a computer in the sidebar to manage that Agent\'s settings.</div>';
    }else renderGlobalSettings();
    return;
  }
  $('#settingsView')?.classList.add('hidden');
  $('#tabSettings')?.classList.remove('active');
  GLOBAL_SETTINGS_BASE_SHOW_VIEW(v);
};
window.openGlobalSettings=function(){showView('settings')};

async function renderGlobalSettings(){
  const root=$('#settingsRoot');if(!root)return;
  root.innerHTML='<div class="muted">Loading Agent settings…</div>';
  try{
    const status=await api('/api/settings/public-relay');
    if(!root.isConnected)return;
    const configured=!!status.configured,reachable=!!status.reachable;
    const stateClass=configured?(reachable?'up':'warn'):'down';
    const stateText=!configured?'Not configured':reachable?'Connected':'Configured, unreachable';
    const tokenHint=status.apiTokenConfigured?'Token is configured — leave blank to keep it.':'Paste the token printed by the VPS Relay installer.';
    root.innerHTML=`<div class="sw-dashboard-head"><div><h2>Settings</h2><div class="muted tiny">Agent-wide settings shared by all game servers.</div></div></div><div class="sw-section"><div class="sw-toolbar"><div><h3 style="margin:0">Public VPS relay</h3><div class="tiny muted">One Relay connection for this Agent. Individual servers choose their public ports in Server → Network.</div></div><div class="spacer"></div><span class="state ${stateClass}">${esc(stateText)}</span></div>${status.error?`<div class="callout">${esc(status.error)}</div>`:''}<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:10px"><div class="field"><label>Relay URL</label><input id="gsRelayUrl" value="${esc(status.relayUrl||'')}" placeholder="http://100.x.y.z:5100"><div class="help">Use the VPS Tailscale address, not its public IP.</div></div><div class="field"><label>Relay API token</label><input id="gsRelayToken" type="password" autocomplete="new-password" placeholder="${esc(tokenHint)}"><div class="help">The saved token is write-only and is never returned to the browser.</div></div><div class="field"><label>Target Tailscale IP</label><input id="gsRelayTarget" value="${esc(status.configuredTargetAddress||'')}" placeholder="Auto-detect${status.targetAddress?' · '+esc(status.targetAddress):''}"><div class="help">Normally leave blank. The Agent then auto-detects its own Tailscale IPv4 address.</div></div><div class="field"><label>VPS public address</label><input id="gsRelayPublicAddress" value="${esc(status.publicAddress||'')}" placeholder="203.0.113.10 or games.example.com"><div class="help">Display value only. Game forwarding still uses the Relay URL/Tailscale path.</div></div></div><div class="sw-info"><div>Relay API</div><div>${esc(stateText)}</div><div>Saved token</div><div>${status.apiTokenConfigured?'Configured':'Not configured'}</div><div>Resolved Agent target</div><div class="mono">${esc(status.targetAddress||'not detected')}</div><div>Traffic model</div><div>Public VPS → DNAT → Tailscale → game server</div></div><div class="checkrow" style="margin-top:12px"><input id="gsRelayClearToken" type="checkbox"><label for="gsRelayClearToken">Clear the saved Relay API token on save</label></div><div class="sw-toolbar" style="margin-top:12px"><button class="btn" ${uiAction('renderGlobalSettings')}>Test / refresh connection</button><div class="spacer"></div><button class="btn primary" ${uiAction('saveGlobalRelaySettings')}>Save Agent relay settings</button></div></div>`;
  }catch(e){root.innerHTML=`<div class="callout">Agent settings failed: ${esc(e.message)}</div>`}
}
window.renderGlobalSettings=renderGlobalSettings;

window.saveGlobalRelaySettings=async function(){
  const relayUrl=$('#gsRelayUrl')?.value.trim()||'';
  const enteredToken=$('#gsRelayToken')?.value.trim()||'';
  const clearToken=!!$('#gsRelayClearToken')?.checked;
  const targetAddress=$('#gsRelayTarget')?.value.trim()||'';
  const publicAddress=$('#gsRelayPublicAddress')?.value.trim()||'';
  try{
    const result=await api('/api/settings/public-relay',{method:'PUT',body:JSON.stringify({relayUrl,relayApiToken:clearToken?'':(enteredToken||null),targetAddress,publicAddress})});
    toast(result?.configured?(result.reachable?'Agent Relay settings saved and connected.':'Agent Relay settings saved, but the VPS is unreachable.'):'Agent Relay settings saved.','ok');
    await renderGlobalSettings();
  }catch(e){toast('Agent Relay settings save failed: '+e.message,'err')}
};

/* Public VPS relay controls. The relay mirrors the manual DNAT + MASQUERADE + FORWARD setup
   but keeps its rules in GSC_RELAY_* chains instead of rewriting /etc/ufw/before.rules. */
function relayProto(v){return String(v||'').toLowerCase()}
function relayWidth(p){return Number(p.targetEndPort??p.targetPort)-Number(p.targetPort)}
function relayDisplayRange(start,end){return Number(start)===Number(end??start)?String(start):`${start}-${end}`}

async function renderPublicRelaySection(def,body){
  const mount=body.querySelector('#swPublicRelay');if(!mount)return;
  let status;
  try{status=await api(`/api/servers/${encodeURIComponent(def.id)}/relay`)}catch(e){if(mount.isConnected)mount.innerHTML=`<div class="callout">Public relay status failed: ${esc(e.message)}</div>`;return}
  if(!mount.isConnected)return;
  const targets=effectiveNetworkPorts(def);
  const routes=targets.map(t=>{
    const found=(status.ports||[]).find(p=>relayProto(p.protocol)===relayProto(t.protocol)&&String(p.name||'').toLowerCase()===String(t.name||'').toLowerCase());
    const targetPort=Number(found?.targetPort??t.port),targetEnd=Number(found?.targetEndPort??t.endPort??t.port),width=targetEnd-targetPort;
    const publicPort=Number(found?.publicPort??t.port),publicEnd=publicPort+width;
    return{name:t.name||'Port',protocol:found?.protocol??t.protocol,targetPort,targetEnd,width,publicPort,publicEnd};
  });
  const running=(STATUS[def.id]?.processState||'')==='Running';
  const configured=!!status.configured,reachable=!!status.reachable,enabled=!!status.enabled;
  const lifecycle=!configured?'Not configured':!reachable?'Relay unreachable':!enabled?'Disabled':running?'Active while server is running':'Enabled, currently closed because server is stopped';
  const stateClass=!configured||!reachable?'down':enabled&&running?'up':enabled?'warn':'down';
  const rows=routes.length?routes.map(p=>`<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(p.name)}</div><div class="sw-row-meta mono">${esc(String(p.protocol).toUpperCase())} target ${esc(relayDisplayRange(p.targetPort,p.targetEnd))}</div></div><div style="min-width:180px"><label class="tiny muted">Public ${p.width?'range start':'port'}</label><input data-relay-port="1" data-name="${esc(p.name)}" data-protocol="${esc(p.protocol)}" data-width="${p.width}" type="number" min="1" max="${65535-p.width}" value="${p.publicPort}" ${configured?'':'disabled'}><div class="tiny muted mono" style="margin-top:3px">${p.width?`maps ${esc(relayDisplayRange(p.publicPort,p.publicEnd))}`:`maps to ${p.targetPort}`}</div></div></div>`).join(''):'<div class="sw-empty">No game ports are defined for this server.</div>';
  const error=status.error?`<div class="callout">${esc(status.error)}</div>`:'';
  const configure=!configured?`<div class="callout info"><b>Relay connection is Agent-wide.</b> Configure the VPS URL/token once in Settings, then return here to expose this server.<div style="margin-top:8px"><button class="btn primary" ${uiAction('openGlobalSettings')}>Open Settings</button></div></div>`:'';
  mount.innerHTML=`<div class="sw-section"><div class="sw-toolbar"><div><h3 style="margin:0">Public VPS relay</h3><div class="tiny muted">Public VPS port → Tailscale → this game server. Rules are installed only while the server is running.</div></div><div class="spacer"></div><span class="state ${stateClass}">${esc(lifecycle)}</span></div>${configure}${error}<div class="sw-info"><div>Relay API</div><div>${configured?(reachable?'Connected':'Configured, unreachable'):'Not configured'}</div><div>Public address</div><div class="mono">${esc(status.publicAddress||'not set')}</div><div>Resolved target</div><div class="mono">${esc(status.targetAddress||'not detected')}</div><div>Traffic model</div><div>DNAT on VPS + MASQUERADE into tailscale0</div></div><div class="callout info" style="margin-top:10px"><b>Client IP:</b> because the VPS performs MASQUERADE, the game server sees the VPS Tailscale address as the peer instead of the player's original public IP. This matches the current manual setup and keeps the return path symmetric.</div><div class="checkrow" style="margin:12px 0"><input id="swRelayEnabled" type="checkbox" ${enabled?'checked':''} ${configured?'':'disabled'}><label for="swRelayEnabled">Expose this server through the public VPS while it is running</label></div>${rows}<div class="sw-toolbar" style="margin-top:12px"><button class="btn" ${configured?'':'disabled'} ${uiAction('serverWorkspaceResetRelayPorts')}>Public ports = game ports</button><div class="spacer"></div><button class="btn primary" ${configured?'':'disabled'} ${uiAction('serverWorkspaceSaveRelay',[def.id])}>Save server relay settings</button></div></div>`;
}

window.serverWorkspaceResetRelayPorts=function(){document.querySelectorAll('[data-relay-port]').forEach(el=>{const row=el.closest('.sw-row'),meta=row?.querySelector('.sw-row-meta')?.textContent||'',m=meta.match(/target\s+(\d+)/i);if(m)el.value=m[1]})};
window.serverWorkspaceSaveRelay=async function(id){
  const enabled=!!$('#swRelayEnabled')?.checked;
  const publicPorts=[];
  for(const el of document.querySelectorAll('[data-relay-port]')){
    const start=Number(el.value),width=Number(el.dataset.width||0),end=start+width;
    if(!Number.isInteger(start)||start<1||end>65535)return toast('Invalid public relay port range.','err');
    publicPorts.push({name:el.dataset.name,protocol:el.dataset.protocol,port:start,endPort:width?end:null});
  }
  try{
    const result=await api(`/api/servers/${encodeURIComponent(id)}/relay`,{method:'PUT',body:JSON.stringify({enabled,publicPorts})});
    toast(result?.configured?(result.reachable?'Server Relay settings saved.':'Server Relay settings saved, but the VPS is unreachable.'):'Server Relay settings saved.','ok');
    const def=SERVERS.find(x=>x.id===id),networkBody=document.querySelector('.sw-workspace[data-tab="network"] .sw-body');
    if(def&&networkBody)renderServerNetwork(def,networkBody);
  }catch(e){toast('Public relay save failed: '+e.message,'err')}
};

window.addEventListener('DOMContentLoaded',()=>{
  installGlobalSettingsView();
  if(typeof renderServerNetwork==='function'&&!renderServerNetwork.__publicRelayWrapped){
    const base=renderServerNetwork;
    const wrapped=function(def,body){base(def,body);const advanced=body.querySelector('details.advanced');const html='<div id="swPublicRelay"><div class="sw-section"><div class="muted">Loading public relay…</div></div></div>';if(advanced)advanced.insertAdjacentHTML('beforebegin',html);else body.insertAdjacentHTML('beforeend',html);renderPublicRelaySection(def,body)};
    wrapped.__publicRelayWrapped=true;renderServerNetwork=wrapped;
  }
  const legacyOpenNetwork=window.openNetwork;
  window.openNetwork=function(id){if(typeof openServerPage==='function')return openServerPage(id,'network');return legacyOpenNetwork?legacyOpenNetwork(id):undefined};
});
/* Official Bedrock add-ons are local file uploads, not arbitrary HTTP URLs.
   Keep the existing mod list/remove workflow; add an explicit import control. */
const minecraftOriginalLoadInstalledMods = loadInstalledMods;
loadInstalledMods = async function(){
  await minecraftOriginalLoadInstalledMods();
  if(MOD_CTX?.def?.gameProfileKey!=='minecraft-bedrock')return;
  const pane=$('#modsPane');
  if(!pane)return;
  pane.insertAdjacentHTML('beforeend',
    '<div class="section">Install Bedrock add-on</div>'+
    '<div class="help">Stop the Bedrock server first. Upload a .mcpack or .mcaddon file, then GSC activates its behavior/resource packs for the current world. Start the server once to create a world before importing the first add-on.</div>'+
    '<div class="toolbar"><input id="bedrockAddonFile" type="file" accept=".mcpack,.mcaddon">'+
    '<button class="btn primary" '+uiAction('installBedrockAddon')+'>Import add-on</button></div>');
};
window.installBedrockAddon=async function(){
  const file=$('#bedrockAddonFile')?.files?.[0];
  if(!file)return toast('Choose a .mcpack or .mcaddon file first.','err');
  if(!/\.(mcpack|mcaddon)$/i.test(file.name))return toast('Only .mcpack or .mcaddon files are supported.','err');
  const id=MOD_CTX?.id;
  if(!id)return;
  const body=new FormData();body.append('file',file,file.name);
  try{
    const result=await api('/api/servers/'+encodeURIComponent(id)+'/minecraft/bedrock/addons',
      {method:'POST',body});
    if(!result.ok)throw new Error(result.error||'Import failed');
    toast('Bedrock add-on activated. Restart the server to apply it.','ok');
    await loadInstalledMods();
  }catch(e){toast('Bedrock add-on import failed: '+e.message,'err')}
};
