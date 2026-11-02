function lines(v){return Array.isArray(v)?v.join('\n'):''}
function splitLines(v){return String(v||'').split(/\r?\n/).map(x=>x.trim()).filter(Boolean)}
function intOrNull(v){const s=String(v||'').trim();if(!s)return null;const n=Number.parseInt(s,10);return Number.isFinite(n)?n:null}
function fieldValue(id,fallback=''){const el=$(id);return el?el.value:fallback}
function expandList(list,root){return (list||[]).map(x=>expandRoot(x,root))}

function openCreate(){renderGamePicker('')}
function renderGamePicker(q=''){
  const list=GAMES.filter(g=>(g.label+' '+g.appId).toLowerCase().includes(q.toLowerCase()));
  openModal('Create server',`<div class="callout info">Choose a game. GameServerControl fills safe defaults automatically and hides unsupported features later.</div><div class="field"><input id="gameSearch" placeholder="Search game…" value="${esc(q)}" ${uiAction('renderGamePicker',[],{event:'input',pass:'value'})}></div><div class="games">${list.map(g=>`<button class="game" ${uiAction('openCreateSettings',[g.key])}><div class="gicon">${g.icon}</div><div class="gname">${esc(g.label)}</div><div class="gmeta">App ${g.appId} · ${esc(g.size)}</div></button>`).join('')}</div>`)
}
function openCreateSettings(key){
  const g=GAMES.find(x=>x.key===key),path=`C:\\GameServers\\${g.dir}`,saves=expandList(g.saveRel,path),log=g.logRel?expandRoot(g.logRel,path):null;
  const features=[g.caps?.config?'Managed config':null,g.caps?.mods?'Mods':null,g.caps?.rcon?'RCON':null,g.caps?.log?'Live log':null].filter(Boolean);
  openModal('Install '+g.label,`<div class="formgrid"><div class="field"><label>Display name</label><input id="cName" value="${esc(g.label)}"></div><div class="field"><label>Server ID</label><input id="cId" value="${esc(slugify(g.label))}"></div></div><div class="field"><label>Server folder</label><input id="cPath" class="mono" value="${esc(path)}"><div class="help">Game files and managed data are placed here where the game supports it.</div></div><div class="profile-summary"><div><b>${g.icon} ${esc(g.label)}</b></div><div class="tiny muted">Steam App ${g.appId}</div><div class="kv compact"><div>Executable</div><div id="cExe" class="mono">${esc(joinPath(path,g.exe))}</div><div>Save data</div><div id="cSaves" class="mono">${saves.map(esc).join('<br>')||'Game default'}</div>${log?`<div>Live log</div><div id="cLog" class="mono">${esc(log)}</div>`:''}</div>${features.length?`<div class="pills">${features.map(x=>`<span class="pill ok">${esc(x)}</span>`).join('')}</div>`:''}${g.note?`<div class="help">${esc(g.note)}</div>`:''}</div><div id="cErr" class="callout hidden"></div>`,`<button class="btn" ${uiAction('renderGamePicker',[''])}>Back</button><button class="btn primary" ${uiAction('startInstall',[g.key])}>Start background install</button>`,true);
  $('#cPath').addEventListener('input',()=>{const p=$('#cPath').value.trim().replace(/[\\/]+$/,'');$('#cExe').textContent=joinPath(p,g.exe);const se=$('#cSaves');if(se)se.innerHTML=expandList(g.saveRel,p).map(esc).join('<br>')||'Game default';const le=$('#cLog');if(le)le.textContent=expandRoot(g.logRel,p)})
}
async function startInstall(key){
  const g=GAMES.find(x=>x.key===key),name=$('#cName').value.trim(),id=$('#cId').value.trim(),path=$('#cPath').value.trim().replace(/[\\/]+$/,''),err=$('#cErr');
  err.classList.add('hidden');
  if(!name||!path||!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id)){err.textContent='Name/path are required and ID may contain lowercase letters, digits and dashes.';err.classList.remove('hidden');return}
  const def={id,name,vmName:'',gameType:g.gameType||'SteamGeneric',guestExePath:joinPath(path,g.exe),guestWorkingDir:path,startArgs:expandList(g.args,path),saveDirs:expandList(g.saveRel,path),steamAppId:g.appId,guestCredentialId:null,logPathInGuest:g.logRel?expandRoot(g.logRel,path):null,rconHost:g.caps?.rcon?'auto':null,rconPort:g.caps?.rcon?(g.rconPort||null):null,rconPassword:null,hostingMode:'BareMetal',scheduledTaskName:null,stopProcessNames:null,discordWebhookUrl:null};
  try{const ack=await api('/api/servers/install',{method:'POST',body:JSON.stringify({steamAppId:g.appId,installPath:path,serverDef:def})});rememberJob({jobId:ack.jobId,name,icon:g.icon,path});closeModal();showView('installs');toast(`${g.label} installation started in background.`,'ok');await refreshJobs()}catch(e){err.textContent=e.message;err.classList.remove('hidden')}
}

function openManage(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;const g=profileFor(d),caps=g?.caps||{},s=STATUS[id]||{};
  const tiles=[];
  if(caps.config)tiles.push(manageTile('⚙','Configuration','Game-friendly settings','openConfig',[id]));
  if(caps.mods)tiles.push(manageTile('🧩','Mods','Browse, install and update','openMods',[id]));
  tiles.push(manageTile('💾','Backups','Create, restore and delete','openBackups',[id]));
  if(caps.console==='log'&&d.logPathInGuest)tiles.push(manageTile('📜','Live console','Follow the server log live','openLog',[id]));
  if(caps.console==='rcon'){
    if(d.rconPort&&d.rconPassword){tiles.push(manageTile('⌨','RCON console','Send live admin commands','openConsole',[id]));if(caps.players)tiles.push(manageTile('👥','Players','View / kick / ban players','openPlayers',[id]))}
    else tiles.push(manageTile('⌨','Set up RCON','Port/password required','openEdit',[id]));
  }
  tiles.push(manageTile('📊','Stats','CPU and RAM history','openStats',[id]));
  tiles.push(manageTile('🗓','Schedule','Restart, update and backups','openSchedule',[id]));
  tiles.push(manageTile('⬆','Update server','Run SteamCMD update','manageAction',[id,'update']));
  if(caps.config)tiles.push(manageTile('✓','Apply config','Apply managed configuration','manageAction',[id,'apply']));
  tiles.push(manageTile('✏','Edit server','Paths and advanced options','openEdit',[id]));
  tiles.push(manageTile('ℹ','Details','Process and runtime details','openDetails',[id]));
  openModal('Manage — '+d.name,`<div class="manage-head"><div><div class="title">${g?.icon||'🖥'} ${esc(d.name)}</div><div class="muted tiny">${esc(g?.label||d.gameType)} · ${d.hostingMode==='Vm'?'Hyper-V':'This machine'} · ${esc(s.processState||'Unknown')}</div></div></div><div class="manage-grid">${tiles.join('')}</div><div class="section">Danger zone</div><button class="btn danger" ${uiAction('deleteServer',[id])}>Remove from GameServerControl</button>`,`<button class="btn" ${uiAction('closeModal')}>Close</button>`,true)
}
function manageTile(icon,title,desc,actionName,args=[]){return `<button class="manage-tile" ${uiAction(actionName,args)}><span class="manage-icon">${icon}</span><span><b>${esc(title)}</b><small>${esc(desc)}</small></span></button>`}
async function manageAction(id,a){closeModal();await action(id,a)}

function openEdit(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;const g=profileFor(d),caps=g?.caps||{},showLog=caps.log||!g,showRcon=caps.rcon||!g;
  const modeOpts=`<option value="BareMetal" ${d.hostingMode!=='Vm'?'selected':''}>This machine (BareMetal)</option><option value="Vm" ${d.hostingMode==='Vm'?'selected':''}>Hyper-V virtual machine</option>`;
  const gameOpts=['SteamGeneric','Windrose','Minecraft','Custom'].map(x=>`<option ${x===d.gameType?'selected':''}>${x}</option>`).join('');
  openModal('Edit — '+d.name,`<div class="callout info">${g?`Using the ${esc(g.label)} profile. Unsupported features are hidden automatically.`:'Custom server: all relevant advanced fields are available.'}</div><div class="section">General</div><div class="formgrid"><div class="field"><label>Name</label><input id="eName" value="${esc(d.name)}"></div><div class="field"><label>Server ID</label><input value="${esc(d.id)}" disabled><div class="help">ID is immutable.</div></div></div><div class="field"><label>Run server on</label><select id="eHosting" ${uiAction('syncEditVisibility',[],{event:'change'})}>${modeOpts}</select></div><div id="eVmSection" class="conditional"><div class="section">Hyper-V</div><div class="formgrid"><div class="field"><label>VM name</label><input id="eVm" value="${esc(d.vmName||'')}"></div><div class="field"><label>Guest credentials</label><input id="eCred" value="${esc(d.guestCredentialId||'default')}"><div class="help">Credential ID configured on the Agent.</div></div></div></div><div class="section">Storage & logs</div><div class="field"><label>Server folder</label><input id="eWork" class="mono" value="${esc(d.guestWorkingDir||'')}"></div><div class="field"><label>Save folders — one per line</label><textarea id="eSave" class="mono">${esc(lines(d.saveDirs))}</textarea><div class="help">These folders are included in GameServerControl backups.</div></div>${showLog?`<div class="field"><label>Log file</label><input id="eLog" class="mono" value="${esc(d.logPathInGuest||'')}"><div class="help">Used by Live console / Live Log.</div></div>`:''}${showRcon?`<div class="section">RCON</div><div class="help">Only shown for games with a supported RCON handler. <b>auto</b> resolves to localhost on this machine or the VM IP on Hyper-V.</div><div class="formgrid three"><div class="field"><label>Host</label><input id="eRhost" value="${esc(d.rconHost||'auto')}"></div><div class="field"><label>Port</label><input id="eRport" type="number" min="1" max="65535" value="${esc(d.rconPort??'')}"></div><div class="field"><label>Password</label><input id="eRpass" type="password" value="${esc(d.rconPassword||'')}"></div></div>`:''}<details class="advanced"><summary>Advanced settings</summary><div class="advanced-body"><div class="help">Normally filled by the game profile. Change only for custom launchers or troubleshooting.</div><div class="field"><label>Server EXE</label><input id="eExe" class="mono" value="${esc(d.guestExePath||'')}"></div><div class="formgrid"><div class="field"><label>Steam App ID</label><input id="eSteam" value="${esc(d.steamAppId||'')}"></div><div class="field"><label>Game type</label><select id="eGameType">${gameOpts}</select></div></div><div class="field"><label>Start args — one per line</label><textarea id="eArgs" class="mono">${esc(lines(d.startArgs))}</textarea></div><div id="eBareSection"><div class="formgrid"><div class="field"><label>Scheduled task</label><input id="eTask" value="${esc(d.scheduledTaskName||'')}"><div class="help">Optional advanced launcher. Usually leave empty.</div></div><div class="field"><label>Extra stop processes</label><textarea id="eStops" class="mono">${esc(lines(d.stopProcessNames))}</textarea><div class="help">Normally leave empty.</div></div></div></div><div class="field"><label>Discord webhook</label><div class="inline"><input id="eDiscord" class="mono" value="${esc(d.discordWebhookUrl||'')}"><button class="btn" type="button" ${uiAction('testDiscordEdit')}>Send test</button></div></div></div></details>`,`<button class="btn" ${uiAction('closeModal')}>Cancel</button><button class="btn primary" ${uiAction('saveEdit',[id])}>Save server</button>`,true);syncEditVisibility()
}
function syncEditVisibility(){const vm=$('#eHosting')?.value==='Vm';const a=$('#eVmSection'),b=$('#eBareSection');if(a)a.classList.toggle('hidden',!vm);if(b)b.classList.toggle('hidden',vm)}
async function saveEdit(id){
  const old=SERVERS.find(x=>x.id===id);if(!old)return;const g=profileFor(old),caps=g?.caps||{},vm=fieldValue('#eHosting',old.hostingMode)==='Vm',showLog=caps.log||!g,showRcon=caps.rcon||!g;
  const def={...old,id:old.id,name:fieldValue('#eName',old.name).trim(),hostingMode:vm?'Vm':'BareMetal',vmName:vm?fieldValue('#eVm',old.vmName||'').trim():'',gameType:fieldValue('#eGameType',old.gameType),steamAppId:fieldValue('#eSteam',old.steamAppId||'').trim()||null,guestExePath:fieldValue('#eExe',old.guestExePath).trim(),guestWorkingDir:fieldValue('#eWork',old.guestWorkingDir).trim(),startArgs:splitLines(fieldValue('#eArgs',lines(old.startArgs))),saveDirs:splitLines(fieldValue('#eSave',lines(old.saveDirs))),guestCredentialId:vm?(fieldValue('#eCred',old.guestCredentialId||'default').trim()||'default'):null,logPathInGuest:showLog?(fieldValue('#eLog',old.logPathInGuest||'').trim()||null):null,rconHost:showRcon?(fieldValue('#eRhost',old.rconHost||'auto').trim()||'auto'):null,rconPort:showRcon?intOrNull(fieldValue('#eRport',old.rconPort??'')):null,rconPassword:showRcon?(fieldValue('#eRpass',old.rconPassword||'')||null):null,scheduledTaskName:!vm?(fieldValue('#eTask',old.scheduledTaskName||'').trim()||null):null,stopProcessNames:!vm?splitLines(fieldValue('#eStops',lines(old.stopProcessNames))):null,discordWebhookUrl:fieldValue('#eDiscord',old.discordWebhookUrl||'').trim()||null};
  if(!def.name||!def.guestExePath||!def.guestWorkingDir)return toast('Name, Server EXE and Server folder are required.','err');
  try{await api(`/api/servers/${encodeURIComponent(id)}`,{method:'PUT',body:JSON.stringify(def)});toast('Server definition saved.','ok');closeModal();await loadServers()}catch(e){toast('Save failed: '+e.message,'err')}
}
async function testDiscordEdit(){const url=$('#eDiscord')?.value.trim();if(!url)return toast('Enter a webhook URL first.','err');try{await api('/api/discord/test',{method:'POST',body:JSON.stringify({webhookUrl:url})});toast('Discord test sent.','ok')}catch(e){toast('Discord test failed: '+e.message,'err')}}
async function deleteServer(id){const d=SERVERS.find(x=>x.id===id);if(!confirm(`Remove "${d?.name||id}" from GameServerControl?\n\nServer files are not deleted.`))return;try{await api(`/api/servers/${encodeURIComponent(id)}`,{method:'DELETE'});toast('Server removed from control panel.','ok');closeModal();await loadServers()}catch(e){toast('Delete failed: '+e.message,'err')}}
async function openDetails(id){const d=SERVERS.find(x=>x.id===id),s=STATUS[id]||{};const meta=Object.entries(s.metadata||{}).map(([k,v])=>`<div>${esc(k)}</div><div>${esc(v)}</div>`).join('');openModal('Details — '+(d?.name||id),`<div class="kv"><div>ID</div><div class="mono">${esc(id)}</div><div>Process</div><div>${esc(s.processState||'Unknown')}</div><div>Host mode</div><div>${d?.hostingMode==='Vm'?'Hyper-V':'This machine'}</div><div>PID</div><div>${s.pidInGuest??'—'}</div><div>CPU</div><div>${s.cpuPercent!=null?Number(s.cpuPercent).toFixed(1)+'%':'—'}</div><div>RAM</div><div>${s.memoryMB!=null?s.memoryMB+' MB':'—'}</div><div>Started</div><div>${s.startedAt?new Date(s.startedAt).toLocaleString():'—'}</div><div>Last backup</div><div>${s.lastBackupAt?new Date(s.lastBackupAt).toLocaleString():'—'}</div><div>Executable</div><div class="mono">${esc(d?.guestExePath||'')}</div><div>Server folder</div><div class="mono">${esc(d?.guestWorkingDir||'')}</div>${meta?'<div class="section" style="grid-column:1/-1">Metadata</div>'+meta:''}</div>`,`<button class="btn" ${uiActions([['loadServers',[]],['openDetails',[id]]])}>Refresh</button><button class="btn" ${uiAction('closeModal')}>Close</button>`,true)}
async function openAutostart(id){try{const d=SERVERS.find(x=>x.id===id),r=await api(`/api/servers/${encodeURIComponent(id)}/autostart`);if(!r.supported)return openModal('Autostart — '+(d?.name||id),'<div class="empty">Autostart via Task Scheduler is an advanced option. Configure a Scheduled Task in Edit → Advanced first.</div>');openModal('Autostart — '+(d?.name||id),`<div class="checkrow"><input id="autoToggle" type="checkbox" ${r.enabled?'checked':''}><label for="autoToggle">Start this game server automatically with Windows</label></div>`,`<button class="btn" ${uiAction('closeModal')}>Cancel</button><button class="btn primary" ${uiAction('saveAutostart',[id])}>Apply</button>`)}catch(e){toast('Autostart failed: '+e.message,'err')}}
async function saveAutostart(id){try{await api(`/api/servers/${encodeURIComponent(id)}/autostart`,{method:'POST',body:JSON.stringify({enabled:$('#autoToggle').checked})});toast('Autostart updated.','ok');closeModal()}catch(e){toast('Autostart update failed: '+e.message,'err')}}
async function openSchedule(id){const d=SERVERS.find(x=>x.id===id);let r=null;try{r=await api(`/api/servers/${encodeURIComponent(id)}/schedule`)}catch{};r=r||{};const days=['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];openModal('Schedule — '+(d?.name||id),`<div class="checkrow"><input id="schRestart" type="checkbox" ${r.dailyRestartEnabled?'checked':''}><label for="schRestart">Daily restart</label></div><div class="field"><label>Restart hour (0–23)</label><input id="schRestartHour" type="number" min="0" max="23" value="${r.dailyRestartHour??5}"></div><div class="checkrow"><input id="schUpdate" type="checkbox" ${r.weeklyUpdateEnabled?'checked':''}><label for="schUpdate">Weekly update</label></div><div class="formgrid"><div class="field"><label>Update day</label><select id="schDay">${days.map(x=>`<option ${x===(r.weeklyUpdateDay||'Wednesday')?'selected':''}>${x}</option>`).join('')}</select></div><div class="field"><label>Update hour (0–23)</label><input id="schUpdateHour" type="number" min="0" max="23" value="${r.weeklyUpdateHour??4}"></div></div><div class="checkrow"><input id="schBackup" type="checkbox" ${r.hourlyBackupEnabled?'checked':''}><label for="schBackup">Hourly backup</label></div><div class="field"><label>Minute (0–59)</label><input id="schBackupMinute" type="number" min="0" max="59" value="${r.hourlyBackupMinute??0}"></div>`,`<button class="btn danger" ${uiAction('clearSchedule',[id])}>Clear schedule</button><button class="btn" ${uiAction('closeModal')}>Cancel</button><button class="btn primary" ${uiAction('saveSchedule',[id])}>Save</button>`)}
async function saveSchedule(id){const body={dailyRestartEnabled:$('#schRestart').checked,dailyRestartHour:Number($('#schRestartHour').value||5),weeklyUpdateEnabled:$('#schUpdate').checked,weeklyUpdateDay:$('#schDay').value,weeklyUpdateHour:Number($('#schUpdateHour').value||4),hourlyBackupEnabled:$('#schBackup').checked,hourlyBackupMinute:Number($('#schBackupMinute').value||0)};try{await api(`/api/servers/${encodeURIComponent(id)}/schedule`,{method:'POST',body:JSON.stringify(body)});toast('Schedule saved.','ok');closeModal()}catch(e){toast('Schedule failed: '+e.message,'err')}}
async function clearSchedule(id){if(!confirm('Clear all maintenance scheduling for this server?'))return;try{await api(`/api/servers/${encodeURIComponent(id)}/schedule`,{method:'DELETE'});toast('Schedule cleared.','ok');closeModal()}catch(e){toast('Clear schedule failed: '+e.message,'err')}}
async function openPlayers(id){const d=SERVERS.find(x=>x.id===id);openModal('Players — '+(d?.name||id),'<div id="playersPane" class="muted">Loading…</div>',`<button class="btn" ${uiAction('loadPlayers',[id])}>Refresh</button><button class="btn" ${uiAction('closeModal')}>Close</button>`,true);await loadPlayers(id)}
async function loadPlayers(id){const p=$('#playersPane');if(!p)return;try{const list=await api(`/api/servers/${encodeURIComponent(id)}/rcon/players`);p.innerHTML=list?.length?list.map(x=>`<div class="player"><div><b>${esc(x.name)}</b><div class="tiny muted">${esc(x.steamId)}</div></div><div class="tiny muted">${esc(x.address||x.playerUid||'')}</div><div><button class="btn" ${uiAction('rconPlayer',[id,'KickPlayer',x.steamId])}>Kick</button><button class="btn danger" ${uiAction('rconPlayer',[id,'BanPlayer',x.steamId])}>Ban</button></div></div>`).join(''):'<div class="empty">No players returned by RCON.</div>'}catch(e){p.innerHTML=`<div class="empty">${esc(e.message)}</div>`}}
async function rconPlayer(id,command,payload){if(command==='BanPlayer'&&!confirm('Ban this player?'))return;try{const r=await api(`/api/servers/${encodeURIComponent(id)}/rcon/command`,{method:'POST',body:JSON.stringify({command,payload})});toast(r.success?(r.output||command+' sent'):(r.error||'RCON failed'),r.success?'ok':'err');await loadPlayers(id)}catch(e){toast('RCON failed: '+e.message,'err')}}
function openConsole(id){const d=SERVERS.find(x=>x.id===id);openModal('RCON console — '+(d?.name||id),`<pre id="consoleOut" class="log">Connected through the Agent RCON handler.\n</pre><div class="consolebar"><input id="consoleIn" class="input mono" placeholder="Raw command" ${uiAction('runConsole',[id],{event:'keydown',key:'Enter'})}><button class="btn primary" ${uiAction('runConsole',[id])}>Send</button></div>`,`<button class="btn" ${uiAction('closeModal')}>Close</button>`,true);setTimeout(()=>$('#consoleIn')?.focus(),0)}
async function runConsole(id){const input=$('#consoleIn'),out=$('#consoleOut'),cmd=input?.value.trim();if(!cmd)return;out.textContent+=`> ${cmd}\n`;input.value='';try{const r=await api(`/api/servers/${encodeURIComponent(id)}/rcon/command`,{method:'POST',body:JSON.stringify({command:'Raw',payload:cmd})});out.textContent+=(r.success?(r.output||'[OK]'):(r.error||'[Failed]'))+'\n';out.scrollTop=out.scrollHeight}catch(e){out.textContent+='[Error] '+e.message+'\n'}}

/* Authenticated identity and ReadOnly UX. Backend authorization remains authoritative. */
let AUTH={id:null,name:null,role:null};
const ADMIN_MUTATION_CALLS=/^(?:action|manageAction|rconPlayer|runConsole|testDiscordEdit|serverWorkspace(?:Save|Create|Restore|Delete|Send|Reset|Clear|PlayerAction|Admin)\w*|gscFiles(?:Save|Restore))$/i;
const ADMIN_MUTATION_PREFIXES=['save','delete','remove','install','update','start','stop','restart','apply','backup','restore','ban','kick','send','clear','create','add'];
const ADMIN_NAV_CALLS=/^(?:openCreate|openAddExisting|openTokens|openUpdate)$/i;
const ADMIN_STATIC_IDS=['updateBtn','createServerBtn','addExistingBtn','tokensBtn'];

function isReadOnly(){return String(AUTH?.role||'').toLowerCase()==='readonly'}
function adminDisabled(){return isReadOnly()?'disabled':''}

async function loadAuthIdentity(){
  AUTH=await api('/api/auth/me');
  window.GSC_AUTH=AUTH;
  return AUTH;
}

function ensureAuthRoleBadge(){
  let badge=document.getElementById('authRole');
  if(!badge){
    badge=document.createElement('span');badge.id='authRole';badge.className='tiny muted';
    const label=document.getElementById('agentLabel');
    if(label)label.insertAdjacentElement('afterend',badge);
  }
  if(!badge)return;
  badge.textContent=AUTH?.role?`· ${AUTH.role}`:'';
  badge.title=AUTH?.name?`${AUTH.name} · ${AUTH.role||''}`:String(AUTH?.role||'');
}

function delegatedActionNames(control){
  const name=control.getAttribute('data-gsc-action')||'';
  if(name!=='runUiActions')return name?[name]:[];
  try{
    const encoded=control.getAttribute('data-gsc-args');
    const args=encoded?JSON.parse(decodeURIComponent(encoded)):[];
    const actions=Array.isArray(args?.[0])?args[0]:[];
    return actions.map(x=>Array.isArray(x)?String(x[0]||''):'').filter(Boolean);
  }catch{return[name]}
}
function delegatedActionRequiresAdmin(control){
  return delegatedActionNames(control).some(name=>{
    const lower=String(name||'').toLowerCase();
    return ADMIN_MUTATION_CALLS.test(name)||ADMIN_NAV_CALLS.test(name)||ADMIN_MUTATION_PREFIXES.some(prefix=>lower.startsWith(prefix));
  });
}

function applyAuthUi(){
  ensureAuthRoleBadge();
  if(!isReadOnly())return;
  const explanation='Read-only access: this action requires an Admin token.';
  document.querySelectorAll('[data-gsc-action]').forEach(control=>{
    if(delegatedActionRequiresAdmin(control)){
      control.disabled=true;
      control.title=explanation;
    }
  });
  for(const id of ADMIN_STATIC_IDS){
    const control=document.getElementById(id);
    if(control){control.disabled=true;control.title=explanation}
  }
}

const FILE_EDITOR_BOUND=new WeakSet();
const FILE_EDITOR_ACTION_ATTRS=['data-gsc-action','data-gsc-args','data-gsc-event','data-gsc-key','data-gsc-pass','data-gsc-cap'];
function applyFilesEditorBindings(){
  const editor=document.getElementById('fseText');
  if(!editor||FILE_EDITOR_BOUND.has(editor))return;
  for(const attr of FILE_EDITOR_ACTION_ATTRS)editor.removeAttribute(attr);
  editor.addEventListener('input',()=>globalThis.gscFilesInput?.());
  editor.addEventListener('keydown',event=>globalThis.gscFilesKeydown?.(event));
  FILE_EDITOR_BOUND.add(editor);
}

const AUTH_BASE_BOOT=boot;
boot=async function(){
  await loadAuthIdentity();
  await AUTH_BASE_BOOT();
  applyAuthUi();
  applyFilesEditorBindings();
  if(isReadOnly()&&typeof setNotice==='function')
    setNotice('Read-only access: viewing is allowed, but changes and interactive commands require an Admin token.','info',true,'Read-only');
};

const AUTH_UI_OBSERVER=new MutationObserver(()=>{applyAuthUi();applyFilesEditorBindings()});
window.addEventListener('DOMContentLoaded',()=>{
  AUTH_UI_OBSERVER.observe(document.body,{childList:true,subtree:true});
  applyAuthUi();
  applyFilesEditorBindings();
});

/* Operational UX extensions. Keep the existing update/stats modules as the transport/rendering
   primitives and add readiness/history once all dependent scripts are available. */
openUpdate=async function(){
  const [s,preflight]=await Promise.all([
    checkForUpdate(true),
    api('/api/update/preflight').catch(e=>({supported:false,ready:false,platform:'Unknown',message:e.message}))
  ]);
  if(!s)return toast('Could not check for updates.','err');
  const available=!!s.updateAvailable;
  const ready=preflight?.ready===true;
  const readiness=ready?'Ready':preflight?.supported===false?'Manual update required':'Not ready';
  const body=`<div class="kv"><div>Running</div><div class="mono">${esc(s.currentVersion||'unknown')}</div><div>Latest</div><div class="mono">${esc(s.latestVersion||'unknown')}</div><div>Checked</div><div>${s.checkedAt?new Date(s.checkedAt).toLocaleString():'—'}</div><div>Platform</div><div>${esc(preflight?.platform||'Unknown')}</div><div>Update readiness</div><div>${esc(readiness)}</div></div><div class="callout ${ready?'info':''}">${esc(preflight?.message||'Update readiness could not be determined.')}</div>${available&&ready?'<div class="callout info">The Agent will restart briefly. Server processes are not stopped.</div>':!available?'<div class="callout info">This Agent is up to date.</div>':''}`;
  const foot=`<button class="btn" ${uiAction('closeModal')}>Close</button>${available&&ready?`<button class="btn primary" ${uiAction('startSelfUpdate')}>Install update</button>`:''}`;
  openModal('GameServerControl update',body,foot);
};

(function installHistoricalStats(){
  let attempts=0;
  const install=()=>{
    if(typeof openStats!=='function'||typeof sampleStats!=='function'||typeof drawStats!=='function'){
      if(++attempts<200)setTimeout(install,10);
      return;
    }
    if(window.__gscHistoricalStatsInstalled)return;
    window.__gscHistoricalStatsInstalled=true;
    openStats=async function(id){
      const d=SERVERS.find(x=>x.id===id);
      STAT_POINTS=[];
      openModal('Stats — '+(d?.name||id),'<div id="statsErr" class="tiny muted">Loading recent Agent history…</div><div id="statsChart"><div class="muted">Collecting samples…</div></div>',`<button class="btn" ${uiAction('closeModal')}>Close</button>`,true);
      try{
        const history=await api(`/api/servers/${encodeURIComponent(id)}/history?limit=90`);
        STAT_POINTS=(history.samples||[]).map(x=>({at:x.at?new Date(x.at).getTime():Date.now(),cpu:Number(x.cpuPercent??0),ram:Number(x.memoryMB??0),pid:x.pidInGuest??null}));
        if(STAT_POINTS.length>90)STAT_POINTS=STAT_POINTS.slice(-90);
        const info=$('#statsErr');if(info)info.textContent=`History ${STAT_POINTS.length}/${history.maxSamples||240} bounded server-side samples · live refresh every 2s`;
        drawStats();
      }catch(e){const info=$('#statsErr');if(info)info.textContent='History unavailable: '+e.message}
      await sampleStats(id);
      const timer=setInterval(()=>sampleStats(id),2000);
      MODAL_CLEANUP=()=>clearInterval(timer);
    };
  };
  setTimeout(install,0);
})();
