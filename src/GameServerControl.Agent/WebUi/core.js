const $=s=>document.querySelector(s);
const esc=s=>(s==null?'':String(s)).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
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
    dir:p.installDirectoryName||p.key,exe:p.executableRelative||'',args:p.defaultStartArgs||[],
    saveRel:p.saveDirs||[],logRel:p.logPath||null,gameType:p.gameType||'SteamGeneric',
    rconPort:p.defaultRconPort??null,note:p.notes||'',installable:!!p.installable,
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
  $('#modalBody').innerHTML='<div class="callout">The Agent did not return within three minutes. Check the Windows service and installer log.</div>';
  $('#modalFoot').innerHTML='<button class="btn" onclick="location.reload()">Reload</button>';
}

function slugify(s){return s.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-|-$/g,'')}
function expandRoot(value,root){return String(value||'').replaceAll('{root}',root.replace(/[\\/]+$/,''))}
function joinPath(root,rel){const value=expandRoot(rel,root);if(/^[a-zA-Z]:[\\/]/.test(value)||value.startsWith('\\\\'))return value;return root.replace(/[\\/]+$/,'')+'\\'+value.replace(/^[\\/]+/,'')}
