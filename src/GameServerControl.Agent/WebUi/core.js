const $=s=>document.querySelector(s);
const esc=s=>(s==null?'':String(s)).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot',"'":'&#39;'}[c]));
const jsq=s=>String(s||'').replace(/\\/g,'\\\\').replace(/'/g,"\\'");
const sleep=ms=>new Promise(r=>setTimeout(r,ms));

let CFG={url:'',token:''};
let SERVERS=[],STATUS={},CURRENT_VIEW='servers',JOB_CACHE={},MOD_CTX=null,MOD_RESULTS=[],MOD_UPDATES=[],MODAL_CLEANUP=null;
let PROFILES=[],GAMES=[],UPDATE_STATUS=null;
const CFG_KEY='gsc.connection.v2';

function toast(msg,kind=''){
  const t=$('#toast');
  if(!t)return;
  t.textContent=msg;t.className='toast show '+kind;
  clearTimeout(toast.t);toast.t=setTimeout(()=>t.className='toast',3600);
}

async function api(path,opts={}){
  const h={Authorization:'Bearer '+CFG.token,...(opts.headers||{})};
  if(opts.body)h['Content-Type']='application/json';
  const r=await fetch(CFG.url+path,{...opts,headers:h});
  const txt=await r.text();let d=null;
  try{d=txt?JSON.parse(txt):null}catch{d=txt}
  if(!r.ok)throw new Error((d&&d.error)||d?.message||d||`${r.status} ${r.statusText}`);
  return d;
}

function cleanupModal(){if(!MODAL_CLEANUP)return;const fn=MODAL_CLEANUP;MODAL_CLEANUP=null;try{fn()}catch{}}
function openModal(title,body,foot='',wide=false){cleanupModal();$('#modalTitle').textContent=title;$('#modalBody').innerHTML=body;$('#modalFoot').innerHTML=foot;$('#modal').classList.toggle('wide',wide);$('#overlay').classList.remove('hidden')}
function closeModal(){cleanupModal();$('#overlay').classList.add('hidden')}
function showView(v){CURRENT_VIEW=v;$('#serversView').classList.toggle('hidden',v!=='servers');$('#installsView').classList.toggle('hidden',v!=='installs');$('#tabServers').classList.toggle('active',v==='servers');$('#tabInstalls').classList.toggle('active',v==='installs');if(v==='installs')refreshJobs()}

function profileToGame(p){
  const c=p.capabilities||{};
  const consoleMode=String(c.consoleMode||'').toLowerCase();
  return {
    key:p.key,label:p.label,icon:p.icon||'🖥',appId:p.steamAppId,size:p.estimatedSize||'varies',
    dir:p.installDirectoryName||p.key,defaultInstallPath:p.defaultInstallPath||p.defaultWindowsInstallPath||'',
    exe:p.hostExecutableRelative||p.executableRelative||'',args:p.hostDefaultStartArgs||p.defaultStartArgs||[],
    saveRel:p.hostSaveDirs||p.saveDirs||[],logRel:p.hostLogPath??p.logPath??null,gameType:p.gameType||'SteamGeneric',
    rconPort:p.defaultRconPort??null,note:p.notes||'',installable:!!p.installable,ports:p.defaultPorts||[],
    caps:{config:!!c.managedConfig,mods:!!c.mods,rcon:!!c.rcon,players:!!c.playerManagement,log:!!c.liveLog,
      console:consoleMode==='rcon'?'rcon':consoleMode==='logonly'?'log':null,hyperV:c.hyperV!==false,scheduledTask:c.scheduledTask!==false}
  };
}

async function loadProfiles(){
  const profiles=await api('/api/game-profiles');
  PROFILES=(profiles||[]).map(profileToGame);
  GAMES=PROFILES.filter(g=>g.installable);
  if(!GAMES.length)throw new Error('Agent returned no installable game profiles.');
}

async function login(){
  const url=$('#loginUrl').value.trim().replace(/\/+$/,'');const token=$('#loginToken').value.trim();
  if(!url||!token)return toast('Agent URL and token are required.','err');
  CFG={url,token};
  try{await api('/api/health');localStorage.setItem(CFG_KEY,JSON.stringify(CFG));await boot()}catch(e){toast('Connection failed: '+e.message,'err')}
}
function logout(){localStorage.removeItem(CFG_KEY);location.reload()}

async function boot(){
  $('#login').classList.add('hidden');$('#app').classList.remove('hidden');$('#agentLabel').textContent='· '+CFG.url;
  try{await loadProfiles()}catch(e){toast('Game profiles failed: '+e.message,'err')}
  await refreshAll();
  await checkForUpdate(false);
  setInterval(()=>{loadServers();refreshJobs(false)},5000);
  setInterval(()=>refreshJobs(false),2000);
  setInterval(()=>checkForUpdate(false),30*60*1000);
}

async function init(){
  const params=new URLSearchParams(location.search);
  const token=params.get('token'),url=params.get('url');
  if(token){CFG={url:(url||location.origin).replace(/\/+$/,''),token};localStorage.setItem(CFG_KEY,JSON.stringify(CFG));history.replaceState({},'',location.pathname+location.hash)}
  else{try{CFG=JSON.parse(localStorage.getItem(CFG_KEY)||'null')||{url:'',token:''}}catch{CFG={url:'',token:''}}}
  if(CFG.url&&CFG.token){try{await api('/api/health');await boot();return}catch{localStorage.removeItem(CFG_KEY)}}
  $('#login').classList.remove('hidden');
  $('#loginUrl').value=location.origin;
}
window.addEventListener('DOMContentLoaded',init);

async function refreshAll(){await Promise.allSettled([loadServers(),refreshJobs(false),checkForUpdate(false)])}
async function loadServers(){try{const[s,st]=await Promise.all([api('/api/servers'),api('/api/status')]);SERVERS=s||[];STATUS={};for(const x of(st||[]))STATUS[x.id]=x;renderServers()}catch(e){toast('Refresh failed: '+e.message,'err')}}
function profileFor(d){return PROFILES.find(g=>g.appId&&g.appId===d.steamAppId)||PROFILES.find(g=>g.gameType===d.gameType&&g.gameType!=='SteamGeneric')||null}
function serverMeta(d){const g=profileFor(d);return g?{icon:g.icon,label:g.label,profile:g}:{icon:'🖥',label:d.gameType||'Server',profile:{caps:{}}}}

function renderServers(){
  const root=$('#servers');
  if(!SERVERS.length){root.innerHTML='<div class="empty">No servers registered yet.<br><br><button class="btn primary" onclick="openCreate()">Create your first server</button> <button class="btn" onclick="openDiscover()">Discover installed servers</button> <button class="btn" onclick="openAddExisting()">Add existing server</button></div>';return}
  root.innerHTML='<div class="grid">'+SERVERS.map(d=>{
    const s=STATUS[d.id]||{},m=serverMeta(d),up=s.processState==='Running',busy=['Starting','Stopping'].includes(s.processState),caps=m.profile.caps||{};
    return `<div class="card"><div class="cardhead"><div class="icon">${m.icon}</div><div class="grow"><div class="title">${esc(d.name)}</div><div class="sub">${esc(m.label)} · ${d.hostingMode==='Vm'?'Hyper-V':'This machine'} · ${esc(d.guestWorkingDir||'')}</div></div><div class="state ${up?'up':busy?'warn':'down'}">${esc(s.processState||'Unknown')}</div></div><div class="metrics"><span>PID <b>${s.pidInGuest??'—'}</b></span><span>CPU <b>${s.cpuPercent!=null?Number(s.cpuPercent).toFixed(1)+'%':'—'}</b></span><span>RAM <b>${s.memoryMB!=null?s.memoryMB+' MB':'—'}</b></span>${caps.rcon&&d.rconPort?`<span>RCON <b>${esc(d.rconHost||'auto')}:${d.rconPort}</b></span>`:''}</div>${s.lastError?`<div class="callout">${esc(s.lastError)}</div>`:''}<div class="actions"><button class="btn" ${up||busy?'disabled':''} onclick="action('${jsq(d.id)}','start')">Start</button><button class="btn danger" ${!up||busy?'disabled':''} onclick="action('${jsq(d.id)}','stop')">Stop</button><button class="btn" ${!up||busy?'disabled':''} onclick="action('${jsq(d.id)}','restart')">Restart</button><button class="btn primary" onclick="openManage('${jsq(d.id)}')">Manage</button></div></div>`
  }).join('')+'</div>';
}

async function action(id,a){toast(`${id}: ${a}…`);try{const r=await api(`/api/servers/${encodeURIComponent(id)}/${a}`,{method:'POST'});toast(r.success?r.message:('Failed: '+r.message),r.success?'ok':'err');setTimeout(loadServers,800)}catch(e){toast(`${a} failed: ${e.message}`,'err')}}

async function checkForUpdate(force=false){
  if(!CFG.token)return null;
  try{
    UPDATE_STATUS=await api(force?'/api/version/check':'/api/version',force?{method:'POST'}:{});
    const btn=$('#updateBtn');
    if(btn){btn.classList.toggle('hidden',!UPDATE_STATUS?.updateAvailable);btn.textContent=UPDATE_STATUS?.updateAvailable?`Update · ${UPDATE_STATUS.latestVersion}`:'Update'}
    return UPDATE_STATUS;
  }catch{return UPDATE_STATUS}
}

async function openUpdate(){
  const s=await checkForUpdate(true);
  if(!s)return toast('Could not check for updates.','err');
  const available=!!s.updateAvailable;
  const body=`<div class="kv"><div>Running</div><div class="mono">${esc(s.currentVersion||'unknown')}</div><div>Latest</div><div class="mono">${esc(s.latestVersion||'unknown')}</div><div>Checked</div><div>${s.checkedAt?new Date(s.checkedAt).toLocaleString():'—'}</div></div>${available?'<div class="callout info">The Agent will restart briefly. Server processes are not stopped.</div>':'<div class="callout info">This Agent is up to date.</div>'}`;
  const foot=`<button class="btn" onclick="closeModal()">Close</button>${available?'<button class="btn primary" onclick="startSelfUpdate()">Install update</button>':''}`;
  openModal('GameServerControl update',body,foot);
}

async function startSelfUpdate(){
  if(!confirm('Update GameServerControl now? The control panel will be unavailable briefly while the Agent restarts. Game servers keep running.'))return;
  $('#modalBody').innerHTML='<div class="empty"><b>Starting updater…</b><br><br>The page will reconnect automatically after the Agent restarts.</div>';$('#modalFoot').innerHTML='';
  try{await api('/api/update',{method:'POST'});await waitForAgentRestart()}catch(e){toast('Update failed to start: '+e.message,'err');closeModal()}
}

async function waitForAgentRestart(){
  const target=UPDATE_STATUS?.latestVersion;let sawOffline=false;
  const deadline=Date.now()+180000;
  while(Date.now()<deadline){
    await sleep(2000);
    try{
      const health=await api('/api/health');
      if(health?.ok){
        const v=await api('/api/version').catch(()=>null);
        if(sawOffline||(target&&v?.currentVersion===target)){location.reload();return}
      }
    }catch{sawOffline=true}
  }
  $('#modalBody').innerHTML='<div class="callout">The Agent did not return within three minutes. Check the service and installer log.</div>';
  $('#modalFoot').innerHTML='<button class="btn" onclick="location.reload()">Reload</button>';
}

function slugify(s){return s.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-|-$/g,'')}
function expandRoot(value,root){return String(value||'').replaceAll('{root}',root.replace(/[\\/]+$/,''))}
function joinPath(root,rel){
  const value=expandRoot(rel,root);
  if(/^[a-zA-Z]:[\\/]/.test(value)||value.startsWith('\\\\')||value.startsWith('/'))return value;
  if(String(root||'').startsWith('/'))return root.replace(/\/+$/,'')+'/'+value.replace(/^[\\/]+/,'').replaceAll('\\','/');
  return root.replace(/[\\/]+$/,'')+'\\'+value.replace(/^[\\/]+/,'');
}

function formatNetworkPort(p){
  const proto=String(p.protocol||'').toUpperCase();
  const start=Number(p.port),end=p.endPort==null?start:Number(p.endPort);
  return `${proto} ${start}${end!==start?'-'+end:''} ${p.name||'Port'}`;
}
function parseNetworkPorts(text){
  const result=[];
  for(const raw of String(text||'').split(/\r?\n/)){
    const line=raw.trim();if(!line||line.startsWith('#'))continue;
    const m=line.match(/^(TCP|UDP)\s+(\d+)(?:-(\d+))?(?:\s+(.+))?$/i);
    if(!m)throw new Error(`Invalid port line: ${line}. Use e.g. "UDP 2456-2457 Game".`);
    const port=Number(m[2]),end=m[3]?Number(m[3]):null;
    if(port<1||port>65535||(end!=null&&(end<port||end>65535)))throw new Error(`Invalid port range: ${line}`);
    result.push({name:(m[4]||'Port').trim(),protocol:m[1].toUpperCase()==='TCP'?'Tcp':'Udp',port,endPort:end});
  }
  return result;
}
function effectiveNetworkPorts(d){const g=profileFor(d);return d.networkPorts??g?.ports??[]}
function rangesOverlap(a,b){const ae=a.endPort==null?Number(a.port):Number(a.endPort),be=b.endPort==null?Number(b.port):Number(b.endPort);return Number(a.port)<=be&&Number(b.port)<=ae}
function findNetworkConflict(ports){
  for(const s of SERVERS){
    if(s.hostingMode==='Vm')continue;
    for(const a of ports||[])for(const b of effectiveNetworkPorts(s)){
      if(String(a.protocol).toLowerCase()===String(b.protocol).toLowerCase()&&rangesOverlap(a,b))return {server:s,ours:a,theirs:b};
    }
  }
  return null;
}
function resetNetworkDefaults(id){const d=SERVERS.find(x=>x.id===id),g=d?profileFor(d):null,ta=$('#networkPorts');if(ta)ta.value=(g?.ports||[]).map(formatNetworkPort).join('\n')}
function profileOwnsNetwork(g){return g?.key==='valheim'||g?.key==='dragonwilds'}
function openNetwork(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;const g=profileFor(d),ports=effectiveNetworkPorts(d),isVm=d.hostingMode==='Vm',isExe=/\.exe$/i.test(d.guestExePath||''),looksWindows=/^[A-Za-z]:[\\/]/.test(d.guestExePath||''),managed=profileOwnsNetwork(g);
  const fw=isVm
    ? '<div class="callout info">Hyper-V has its own guest network stack. GameServerControl does not change the guest Windows Firewall from the host.</div>'
    : !looksWindows
      ? '<div class="callout info">Automatic program firewall rules apply to Windows BareMetal servers only.</div>'
      : isExe
        ? `<div class="callout info"><b>Windows Firewall:</b> GameServerControl manages persistent inbound and outbound Allow rules for <span class="mono">${esc(d.guestExePath)}</span>. The ports below are currently used for collision detection, not firewall filtering.</div>`
        : '<div class="callout info">This server launches through a script/JAR rather than an .exe, so a reliable Windows program rule cannot be created yet. Port reservations still prevent accidental conflicts.</div>';
  const managedHint=managed?`<div class="callout info"><b>${esc(g.label)} ports are profile-managed.</b> Change them in Configuration. GameServerControl will update the game launch arguments and these reservations together.</div>`:'';
  const defaults=!managed&&(g?.ports||[]).length?`<button class="btn" onclick="resetNetworkDefaults('${jsq(id)}')">Reset to ${esc(g.label)} defaults</button>`:'';
  const readOnly=managed?' readonly':'';
  const footer=managed
    ? `<button class="btn" onclick="closeModal()">Close</button><button class="btn primary" onclick="closeModal();openConfig('${jsq(id)}')">Edit Configuration</button>`
    : `<button class="btn" onclick="closeModal()">Cancel</button><button class="btn primary" onclick="saveNetwork('${jsq(id)}')">Save network settings</button>`;
  openModal('Network — '+d.name,`${fw}${managedHint}<div class="section">Reserved/listening ports</div><div class="field"><label>One port or range per line</label><textarea id="networkPorts" class="mono" style="min-height:180px"${readOnly}>${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Format: <span class="mono">UDP 2456-2457 Game</span> or <span class="mono">TCP 7777 Game/API</span>. BareMetal servers on this Agent may not reserve overlapping ports of the same protocol.</div></div>${defaults}<div class="section">Profile defaults</div><div class="tiny muted">${(g?.ports||[]).length?(g.ports.map(formatNetworkPort).map(esc).join('<br>')):'No curated port defaults are defined for this game yet.'}</div>`,footer,true)
}
async function saveNetwork(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;
  const g=profileFor(d);if(profileOwnsNetwork(g))return toast(`${g.label} ports must be changed in Configuration so launch arguments stay synchronized.`,'err');
  let ports;try{ports=parseNetworkPorts($('#networkPorts')?.value||'')}catch(e){return toast(e.message,'err')}
  try{await api(`/api/servers/${encodeURIComponent(id)}`,{method:'PUT',body:JSON.stringify({...d,networkPorts:ports})});toast('Network settings saved.','ok');closeModal();await loadServers()}catch(e){toast('Network settings rejected: '+e.message,'err')}
}

function readInstallPorts(g){
  const copy=(g?.ports||[]).map(p=>({...p}));
  if(g?.key==='valheim'){
    const port=Number.parseInt($('#cGamePort')?.value||'2456',10);
    if(!Number.isInteger(port)||port<1024||port>65534)throw new Error('Valheim game port must be between 1024 and 65534; Valheim also uses port+1.');
    return [{name:'Game',protocol:'Udp',port,endPort:port+1}];
  }
  if(g?.key==='dragonwilds'){
    const port=Number.parseInt($('#cGamePort')?.value||'7777',10),beacon=Number.parseInt($('#cBeaconPort')?.value||'8888',10);
    if(!Number.isInteger(port)||port<1024||port>65535)throw new Error('Dragonwilds game port must be between 1024 and 65535.');
    if(!Number.isInteger(beacon)||beacon<1024||beacon>65535)throw new Error('Dragonwilds beacon port must be between 1024 and 65535.');
    if(port===beacon)throw new Error('Dragonwilds game and beacon ports must be different.');
    return [{name:'Game',protocol:'Udp',port,endPort:null},{name:'Beacon',protocol:'Udp',port:beacon,endPort:null}];
  }
  return copy;
}
function applyInstallPorts(g,args,ports){
  const out=[...(args||[])];
  if(g?.key==='valheim'){
    const port=ports[0]?.port;
    const i=out.findIndex(x=>String(x).toLowerCase()==='-port');
    if(i>=0&&i+1<out.length)out[i+1]=String(port);else out.push('-port',String(port));
  }else if(g?.key==='dragonwilds'){
    const game=ports.find(p=>String(p.name).toLowerCase()==='game')?.port,beacon=ports.find(p=>String(p.name).toLowerCase()==='beacon')?.port;
    const setInline=(prefix,value)=>{const i=out.findIndex(x=>String(x).toLowerCase().startsWith(prefix.toLowerCase()));if(i>=0)out[i]=prefix+value;else out.push(prefix+value)};
    setInline('-Port=',game);setInline('-BeaconPort=',beacon);
  }
  return out;
}

function installNetworkUiHooks(){
  if(typeof openManage==='function'&&!openManage.__networkWrapped){
    const original=openManage;
    const wrapped=function(id){
      original(id);
      const grid=document.querySelector('.manage-grid');
      if(grid&&!grid.querySelector('[data-network-tile]')){
        const b=document.createElement('button');b.className='manage-tile';b.dataset.networkTile='1';b.onclick=()=>openNetwork(id);
        b.innerHTML='<span class="manage-icon">🌐</span><span><b>Network & firewall</b><small>Ports, conflicts and Windows Firewall</small></span>';
        grid.appendChild(b);
      }
    };
    wrapped.__networkWrapped=true;window.openManage=wrapped;
  }
  if(typeof openCreateSettings==='function'&&!openCreateSettings.__networkWrapped){
    const originalCreate=openCreateSettings;
    const wrappedCreate=function(key){
      originalCreate(key);
      const g=GAMES.find(x=>x.key===key),body=$('#modalBody');
      const path=$('#cPath');
      if(path&&g?.defaultInstallPath){path.value=g.defaultInstallPath;path.dispatchEvent(new Event('input'))}
      if(!body)return;
      if(g?.key==='valheim'){
        const box=document.createElement('div');box.className='formgrid';box.innerHTML='<div class="field"><label>Game port</label><input id="cGamePort" type="number" min="1024" max="65534" value="2456"><div class="help">Valheim reserves this UDP port and the next one (port+1).</div></div>';body.appendChild(box);
      }else if(g?.key==='dragonwilds'){
        const box=document.createElement('div');box.className='formgrid';box.innerHTML='<div class="field"><label>Game port</label><input id="cGamePort" type="number" min="1024" max="65535" value="7777"></div><div class="field"><label>Beacon port</label><input id="cBeaconPort" type="number" min="1024" max="65535" value="8888"></div>';body.appendChild(box);
      }else if(g?.ports?.length){
        const box=document.createElement('div');box.className='callout info';box.innerHTML='<b>Default network ports:</b><br>'+g.ports.map(formatNetworkPort).map(esc).join('<br>')+'<br><span class="tiny muted">GameServerControl checks these against other BareMetal servers before registration.</span>';body.appendChild(box)
      }
    };
    wrappedCreate.__networkWrapped=true;window.openCreateSettings=wrappedCreate;
  }
  if(typeof openEdit==='function'&&!openEdit.__profileHostingWrapped){
    const originalEdit=openEdit;
    const wrappedEdit=function(id){
      originalEdit(id);
      const d=SERVERS.find(x=>x.id===id),g=d?profileFor(d):null,sel=$('#eHosting');
      if(g?.caps?.hyperV===false&&sel){
        sel.innerHTML='<option value="BareMetal">This machine (BareMetal)</option>';sel.value='BareMetal';syncEditVisibility();
        if(d?.hostingMode==='Vm'){
          const body=$('#modalBody'),warn=document.createElement('div');warn.className='callout';warn.textContent=`${g.label} no longer supports Hyper-V. Move/reinstall this server on the Agent host and correct its paths before saving.`;body?.prepend(warn)
        }
      }
    };
    wrappedEdit.__profileHostingWrapped=true;window.openEdit=wrappedEdit;
  }
  if(typeof startInstall==='function'&&!startInstall.__networkWrapped){
    const wrappedStart=async function(key){
      const g=GAMES.find(x=>x.key===key),name=$('#cName')?.value.trim(),id=$('#cId')?.value.trim(),path=$('#cPath')?.value.trim().replace(/[\\/]+$/,''),err=$('#cErr');
      err?.classList.add('hidden');
      if(!g||!name||!path||!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id||'')){if(err){err.textContent='Name/path are required and ID may contain lowercase letters, digits and dashes.';err.classList.remove('hidden')}return}
      let ports;try{ports=readInstallPorts(g)}catch(e){if(err){err.textContent=e.message;err.classList.remove('hidden')}return}
      const conflict=findNetworkConflict(ports);
      if(conflict){const msg=`Port conflict with ${conflict.server.name}: ${formatNetworkPort(conflict.ours)} overlaps ${formatNetworkPort(conflict.theirs)}.`;if(err){err.textContent=msg;err.classList.remove('hidden')}else toast(msg,'err');return}
      const args=applyInstallPorts(g,expandList(g.args,path),ports);
      const def={id,name,vmName:'',gameType:g.gameType||'SteamGeneric',guestExePath:joinPath(path,g.exe),guestWorkingDir:path,startArgs:args,saveDirs:expandList(g.saveRel,path),steamAppId:g.appId,guestCredentialId:null,logPathInGuest:g.logRel?expandRoot(g.logRel,path):null,rconHost:g.caps?.rcon?'auto':null,rconPort:g.caps?.rcon?(g.rconPort||null):null,rconPassword:null,hostingMode:'BareMetal',scheduledTaskName:null,stopProcessNames:null,discordWebhookUrl:null,networkPorts:ports};
      try{const ack=await api('/api/servers/install',{method:'POST',body:JSON.stringify({steamAppId:g.appId,installPath:path,serverDef:def})});rememberJob({jobId:ack.jobId,name,icon:g.icon,path});closeModal();showView('installs');toast(`${g.label} installation started in background.`,'ok');await refreshJobs()}catch(e){if(err){err.textContent=e.message;err.classList.remove('hidden')}else toast(e.message,'err')}
    };
    wrappedStart.__networkWrapped=true;window.startInstall=wrappedStart;
  }
}
window.addEventListener('DOMContentLoaded',installNetworkUiHooks);
