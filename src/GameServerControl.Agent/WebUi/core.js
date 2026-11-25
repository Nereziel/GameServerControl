const $=s=>document.querySelector(s);
const esc=s=>(s==null?'':String(s)).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const sleep=ms=>new Promise(r=>setTimeout(r,ms));

const uiAction=(()=>{
  const cap=(globalThis.crypto?.randomUUID?.()||`${Date.now()}-${Math.random()}`).replace(/[^a-zA-Z0-9-]/g,'');
  const encode=value=>encodeURIComponent(JSON.stringify(value));
  const decode=value=>JSON.parse(decodeURIComponent(value));
  const attr=(name,value)=>`${name}="${esc(value)}"`;

  function render(name,args=[],opts={}){
    const attrs=[attr('data-gsc-cap',cap),attr('data-gsc-action',name)];
    if(args.length)attrs.push(attr('data-gsc-args',encode(args)));
    if(opts.event&&opts.event!=='click')attrs.push(attr('data-gsc-event',opts.event));
    if(opts.key)attrs.push(attr('data-gsc-key',opts.key));
    if(opts.pass)attrs.push(attr('data-gsc-pass',opts.pass));
    if(opts.selector)attrs.push(attr('data-gsc-selector',opts.selector));
    if(opts.self)attrs.push('data-gsc-self="1"');
    return attrs.join(' ');
  }

  async function dispatch(event){
    const el=event.target?.closest?.('[data-gsc-action]');
    if(!el||el.getAttribute('data-gsc-cap')!==cap)return;
    const expected=el.getAttribute('data-gsc-event')||'click';
    if(event.type!==expected)return;
    const key=el.getAttribute('data-gsc-key');
    if(key&&event.key!==key)return;
    if(el.getAttribute('data-gsc-self')==='1'&&event.target!==el)return;
    if(el.disabled)return;

    const name=el.getAttribute('data-gsc-action');
    const fn=globalThis[name];
    if(typeof fn!=='function')return;
    let args=[];
    const encoded=el.getAttribute('data-gsc-args');
    if(encoded){try{args=decode(encoded)}catch{return}}
    const pass=el.getAttribute('data-gsc-pass');
    if(pass==='value')args.push(el.value);
    else if(pass==='checked')args.push(!!el.checked);
    else if(pass==='element')args.push(el);
    else if(pass==='event')args.push(event);
    else if(pass==='selectorValue'){
      const selector=el.getAttribute('data-gsc-selector');
      args.push(selector?document.querySelector(selector)?.value:undefined);
    }
    try{await fn(...args)}catch(e){console.error('UI action failed',name,e)}
  }

  for(const type of ['click','change','input','keydown'])document.addEventListener(type,dispatch);
  return render;
})();

function uiActions(actions,opts={}){return uiAction('runUiActions',[actions],opts)}
async function runUiActions(actions){for(const [name,args=[]] of actions||[]){const fn=globalThis[name];if(typeof fn==='function')await fn(...args)}}

let CFG={url:'',token:''};
let SERVERS=[],STATUS={},CURRENT_VIEW='servers',JOB_CACHE={},MOD_CTX=null,MOD_RESULTS=[],MOD_UPDATES=[],MODAL_CLEANUP=null;
let PROFILES=[],GAMES=[],UPDATE_STATUS=null;
let HOST_CAPABILITIES={schemaVersion:0,features:[]};
const CFG_KEY='gsc.connection.v2';

const HOST_KEY='gsc.activeHost.v1';
let ACTIVE_HOST_ID='local',HOST_DIRECTORY=[],HOST_OVERVIEW=[],HOST_SWITCH_EPOCH=0,LAST_HOST_OVERVIEW=0;

function agentApiPath(path){
  if(ACTIVE_HOST_ID==='local'||ACTIVE_HOST_ID==='all'||!path.startsWith('/api/')||
      path==='/api/hosts'||path.startsWith('/api/hosts/')||path==='/api/auth/me')
    return path;
  return '/api/hosts/'+encodeURIComponent(ACTIVE_HOST_ID)+path;
}

function setHostChrome(){
  const all=ACTIVE_HOST_ID==='all';
  for(const id of ['createServerBtn','discoverBtn','addExistingBtn',...(window.GSC_CLOUD_MODE ? ['updateBtn','tokensBtn'] : [])]){
    const button=$('#'+id);
    if(button){
      button.disabled=all||!!(typeof isReadOnly==='function'&&isReadOnly());
      button.title=all?'Select a specific computer to change its servers.':'';
    }
  }
  const select=$('#hostSwitch');
  if(select&&select.value!==ACTIVE_HOST_ID)select.value=ACTIVE_HOST_ID;
}

async function refreshManagedHosts(){
  const hosts=await api('/api/hosts');
  HOST_DIRECTORY=Array.isArray(hosts)?hosts:[];
  if(ACTIVE_HOST_ID!=='all'&&!(ACTIVE_HOST_ID==='local'&&!window.GSC_CLOUD_MODE)&&
      !HOST_DIRECTORY.some(h=>h.id===ACTIVE_HOST_ID))
    ACTIVE_HOST_ID=window.GSC_CLOUD_MODE?'all':'local';
  const select=$('#hostSwitch');
  if(select){
    select.innerHTML='<option value="all" data-gsc-i18n="All computers">All computers</option>'+HOST_DIRECTORY.map(h=>
      `<option value="${esc(h.id)}">${esc(h.isLocal?'This computer · '+h.name:h.name)}</option>`).join('');
    select.value=ACTIVE_HOST_ID;
  }
  setHostChrome();
}

async function selectHost(id){
  if(id!=='all'&&!HOST_DIRECTORY.some(h=>h.id===id))return toast('Unknown computer.','err');
  if(id===ACTIVE_HOST_ID)return;
  if(typeof SERVER_WORKSPACE!=='undefined' && SERVER_WORKSPACE.serverId &&
      typeof serverWorkspaceConfirmLeave==='function' && !serverWorkspaceConfirmLeave()){
    const select=$('#hostSwitch');
    if(select)select.value=ACTIVE_HOST_ID;
    return;
  }
  stopStatusStream();
  ACTIVE_HOST_ID=id;
  HOST_SWITCH_EPOCH++;
  LAST_HOST_OVERVIEW=0;
  sessionStorage.setItem(HOST_KEY,id);
  if(typeof serverWorkspaceCleanupConsole==='function')serverWorkspaceCleanupConsole();
  if(typeof SERVER_WORKSPACE!=='undefined'){SERVER_WORKSPACE.serverId=null;SERVER_WORKSPACE.tab='overview';}
  SERVERS=[];STATUS={};HOST_OVERVIEW=[];JOB_CACHE={};GSCUX.actions={};
  GSCUX.selected=null;
  if(location.hash!=='#servers')location.hash='servers';
  showView('servers');
  setHostChrome();
  try{
    if(id!=='all'){
      await loadCapabilities();
      try{await loadProfiles()}catch(e){setNotice('Profile refresh failed: '+e.message,'err',false)}
    }else HOST_CAPABILITIES={schemaVersion:0,features:[]};
    await loadServers(false);
    await refreshJobs(false);
    startStatusStream();
  }catch(e){setNotice('Computer connection failed: '+e.message,'err',false)}
}
async function selectHostAndOpen(hostId,serverId){
  await selectHost(hostId);
  if(ACTIVE_HOST_ID!==hostId)return;
  const found=SERVERS.some(s=>s.id===serverId);
  if(found)openServerPage(serverId,'overview');
  else toast('The selected computer is unavailable or the server was removed.','err');
}
async function openManagedHosts(){
  try{await refreshManagedHosts()}catch(e){return toast(e.message,'err')}
  const rows=HOST_DIRECTORY.filter(h=>!h.isLocal).map(h=>
    `<div class="host-managed-row"><span><b>${esc(h.name)}</b><small class="mono">${esc(h.id)}</small></span><button class="btn danger" ${uiAction('removeManagedHost',[h.id])} data-gsc-i18n="Remove">Remove</button></div>`).join('')||
    '<div class="tiny muted" data-gsc-i18n="No additional computers registered yet.">No additional computers registered yet.</div>';
  const cloud=!!window.GSC_CLOUD_MODE;
  const form=`<div class="host-security-note">${cloud?
    'Install Agent on each PC, give it a Tailscale listener and configure its route in the VPS Caddy proxy. The route must match the Computer ID below. Tokens are encrypted in Cloudflare D1; one offline PC does not affect any other.':
    'Install GameServerControl Agent on the second PC first. Create one Admin and one ReadOnly token there. Connect via Tailscale or a TLS-protected private Agent URL. Tokens stay encrypted on this coordinating Agent, never in your browser after saving.'}</div>
    <div class="host-form"><div class="formgrid"><div class="field"><label data-gsc-i18n="Computer ID">Computer ID</label><input id="hostNewId" placeholder="gaming-pc-2" maxlength="40" autocomplete="off"></div>
    <div class="field"><label data-gsc-i18n="Display name">Display name</label><input id="hostNewName" placeholder="Gaming PC 2" maxlength="80" autocomplete="off"></div></div>
    ${cloud?'':`<div class="field"><label data-gsc-i18n="Agent address">Agent address</label><input id="hostNewOrigin" type="url" placeholder="https://pc2.example.ts.net:5100 or http://100.100.1.2:5099" autocomplete="off"></div>`}
    <div class="formgrid"><div class="field"><label data-gsc-i18n="Agent Admin token">Agent Admin token</label><input id="hostNewAdmin" type="password" autocomplete="new-password" spellcheck="false"></div>
    <div class="field"><label data-gsc-i18n="Agent ReadOnly token">Agent ReadOnly token</label><input id="hostNewReadonly" type="password" autocomplete="new-password" spellcheck="false"></div></div>
    <div class="help">Credentials are verified against the remote Agent before saving. ${cloud?
      'Cloudflare always contacts the preconfigured HTTPS VPS gateway, never a browser-supplied origin.':
      'No unauthenticated or unencrypted public HTTP connection is allowed.'}</div>
    <button class="btn primary" ${uiAction('saveManagedHost')} data-gsc-i18n="Connect computer">Connect computer</button></div>`;
  openModal('Manage computers',
    '<div class="sw-section"><h3 data-gsc-i18n="Connected computers">Connected computers</h3>'+rows+'</div><div class="sw-section"><h3 data-gsc-i18n="Add a physical computer">Add a physical computer</h3>'+form+'</div>',
    `<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Close">Close</button>`,true);
}
async function saveManagedHost(){
  const id=$('#hostNewId')?.value.trim()||'';
  const name=$('#hostNewName')?.value.trim()||'';
  const origin=$('#hostNewOrigin')?.value.trim()||'';
  const adminToken=$('#hostNewAdmin')?.value.trim()||'';
  const readOnlyToken=$('#hostNewReadonly')?.value.trim()||'';
  if(!id||!name||(!window.GSC_CLOUD_MODE&&!origin)||!adminToken||!readOnlyToken)
    return toast('All computer connection fields are required.','err');
  const button=$('#hostNewAdmin')?.closest('.host-form')?.querySelector('button.primary');
  if(button)button.disabled=true;
  try{
    await api('/api/hosts',{method:'POST',body:JSON.stringify({id,name,origin,adminToken,readOnlyToken})});
    for(const inputId of ['hostNewAdmin','hostNewReadonly']){
      const input=$('#'+inputId);if(input)input.value='';
    }
    await refreshManagedHosts();
    closeModal();
    toast('Computer registered.','ok');
    await selectHost(id);
  }catch(e){toast('Unable to register computer: '+e.message,'err')}
  finally{if(button)button.disabled=false}
}
async function removeManagedHost(id){
  if(!confirm('Remove '+id+' from the panel? The remote Agent and its game servers will not be stopped.'))return;
  try{
    await api('/api/hosts/'+encodeURIComponent(id),{method:'DELETE'});
    await refreshManagedHosts();
    if(ACTIVE_HOST_ID===id)await selectHost('all');
    else {LAST_HOST_OVERVIEW=0;await loadServers(true)}
    closeModal();
    toast('Computer disconnected from the panel.','ok');
  }catch(e){toast('Remove failed: '+e.message,'err')}
}
window.selectHostAndOpen=selectHostAndOpen;
window.saveManagedHost=saveManagedHost;
window.removeManagedHost=removeManagedHost;



function toast(msg,kind=''){
  const t=$('#toast');
  if(!t)return;
  t.textContent=msg;t.className='toast show '+kind;
  clearTimeout(toast.t);toast.t=setTimeout(()=>t.className='toast',3600);
}

async function api(path,opts={}){
  const h={Authorization:'Bearer '+CFG.token,...(opts.headers||{})};
  if(opts.body && !(opts.body instanceof FormData))h['Content-Type']='application/json';
  const r=await fetch(CFG.url+agentApiPath(path),{...opts,headers:h});
  const txt=await r.text();let d=null;
  try{d=txt?JSON.parse(txt):null}catch{d=txt}
  if(!r.ok)throw new Error((d&&d.error)||d?.message||d||`${r.status} ${r.statusText}`);
  return d;
}

function cleanupModal(){if(!MODAL_CLEANUP)return;const fn=MODAL_CLEANUP;MODAL_CLEANUP=null;try{fn()}catch{}}
function openModal(title,body,foot='',wide=false){cleanupModal();$('#modalTitle').textContent=title;$('#modalBody').innerHTML=body;$('#modalFoot').innerHTML=foot;$('#modal').classList.toggle('wide',wide);$('#overlay').classList.remove('hidden')}
function closeModal(){cleanupModal();$('#overlay').classList.add('hidden')}
function showView(v){
  if (!['servers','installs','system'].includes(v)) return;
  CURRENT_VIEW=v;
  // Keep a stable direct link for non-server pages without re-triggering hash handlers.
  if (v==='system' || v==='installs') {
    if (location.hash !== '#'+v) history.replaceState(null,'','#'+v);
  } else if (location.hash==='#system' || location.hash==='#installs') {
    history.replaceState(null,'','#servers');
  }
  $('#serversView').classList.toggle('hidden',v!=='servers');
  $('#installsView').classList.toggle('hidden',v!=='installs');
  $('#systemView').classList.toggle('hidden',v!=='system');
  for (const [name,id] of [['servers','tabServers'],['installs','tabInstalls'],['system','tabSystem']]) {
    const nav=$('#'+id);
    nav?.classList.toggle('active',v===name);
    if(v===name)nav?.setAttribute('aria-current','page');
    else nav?.removeAttribute('aria-current');
  }
  setWorkspaceHeading(v==='installs'?'Installations':v==='system'?'Settings':'Servers',
    v==='installs'?'CONTROL PANEL / ACTIVITY':v==='system'?'CONTROL PANEL / ADMINISTRATION':'CONTROL PANEL / OVERVIEW');
  closeMobileNavigation();
  if(v==='installs')refreshJobs();
  if(v==='system')renderSystemView();
}

function renderSystemView(){
  const hosts=$('#systemHostSummary');
  if(hosts){
    const known=Array.isArray(HOST_DIRECTORY)?HOST_DIRECTORY:[];
    const active=known.find(host=>host.id===ACTIVE_HOST_ID);
    const label=ACTIVE_HOST_ID==='all'?'All computers':active?.name||'This computer';
    hosts.textContent=known.length+' registered computer'+(known.length===1?'':'s')+' · Active: '+label;
  }
  const version=$('#systemVersionSummary');
  if(version){
    const current=UPDATE_STATUS?.currentVersion||'Unknown';
    const available=UPDATE_STATUS?.updateAvailable;
    version.textContent='Agent version: '+current+(available?' · Update available: '+(UPDATE_STATUS.latestVersion||'new version'):'');
  }
  if(CURRENT_VIEW==='system' && typeof renderGlobalSettings==='function')renderGlobalSettings();
}

function openAddServerMenu(){
  if(ACTIVE_HOST_ID==='all')
    return toast('Select a computer before adding a game server.','err');
  openModal('Add a server',
    `<div class="system-action-grid">
      <button type="button" class="system-choice" ${uiAction('openCreate')}><b data-gsc-i18n="Install a new game">Install a new game</b><span data-gsc-i18n="Choose a supported game and start an installation.">Choose a supported game and start an installation.</span></button>
      <button type="button" class="system-choice" ${uiAction('openAddExisting')}><b data-gsc-i18n="Add an existing server">Add an existing server</b><span data-gsc-i18n="Register an installation without touching its game files.">Register an installation without touching its game files.</span></button>
      <button type="button" class="system-choice" ${uiAction('openDiscover')}><b data-gsc-i18n="Discover installations">Discover installations</b><span data-gsc-i18n="Scan known libraries and server directories.">Scan known libraries and server directories.</span></button>
    </div>`,
    `<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Cancel">Cancel</button>`,true);
}

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
      console:consoleMode==='rcon'?'rcon':consoleMode==='logonly'?'log':consoleMode==='stdin'?'stdin':null,hyperV:c.hyperV!==false,scheduledTask:c.scheduledTask!==false}
  };
}

async function loadCapabilities(){
  if(ACTIVE_HOST_ID==='all'){HOST_CAPABILITIES={schemaVersion:0,features:[]};return}
  const selectedHost=ACTIVE_HOST_ID;
  let capabilities={schemaVersion:0,features:[]};
  try{
    const payload=await api('/api/capabilities');
    if(payload&&Number.isInteger(payload.schemaVersion)&&Array.isArray(payload.features)){
      capabilities={schemaVersion:payload.schemaVersion,
        features:payload.features.filter(feature=>typeof feature==='string')};
    }
  }catch{
    // Older Agents do not implement capabilities; do not offer new-only actions.
  }
  if(selectedHost===ACTIVE_HOST_ID)HOST_CAPABILITIES=capabilities;
}
function supportsAgentFeature(name){
  return HOST_CAPABILITIES.schemaVersion>=1&&HOST_CAPABILITIES.features.includes(name);
}

async function loadProfiles(){
  const profiles=await api('/api/game-profiles');
  PROFILES=(profiles||[]).map(profileToGame);
  GAMES=PROFILES.filter(g=>g.installable);
  if(!GAMES.length)throw new Error('Agent returned no installable game profiles.');
}


// A non-exportable private key is persisted by the browser's origin-scoped
// IndexedDB. No reusable API bearer or remember token is stored persistently.
function openLocalBrowserKeyDb(){
  return new Promise((resolve,reject)=>{
    if(!window.indexedDB||!crypto?.subtle)return reject(new Error('Secure browser storage unavailable.'));
    const req=indexedDB.open('gsc-local-browser-v1',1);
    const timer=setTimeout(()=>reject(new Error('Browser key storage timed out.')),3000);
    req.onblocked=()=>{clearTimeout(timer);reject(new Error('Browser key storage is blocked.'))};
    req.onupgradeneeded=()=>{if(!req.result.objectStoreNames.contains('keys'))req.result.createObjectStore('keys')};
    req.onsuccess=()=>{clearTimeout(timer);resolve(req.result)};
    req.onerror=()=>{clearTimeout(timer);reject(req.error||new Error('Could not access browser key storage.'))};
  });
}
async function localBrowserKeyRead(){
  const db=await openLocalBrowserKeyDb();
  try{return await new Promise((resolve,reject)=>{
    const request=db.transaction('keys','readonly').objectStore('keys').get('admin');
    request.onsuccess=()=>resolve(request.result||null);
    request.onerror=()=>reject(request.error);
  })}finally{db.close()}
}
async function localBrowserKeyWrite(value){
  const db=await openLocalBrowserKeyDb();
  try{await new Promise((resolve,reject)=>{
    const tx=db.transaction('keys','readwrite');
    tx.objectStore('keys').put(value,'admin');
    tx.oncomplete=()=>resolve();
    tx.onerror=()=>reject(tx.error||new Error('Browser key write failed.'));
    tx.onabort=()=>reject(tx.error||new Error('Browser key write aborted.'));
  })}finally{db.close()}
}
async function localBrowserKeyDelete(){
  const db=await openLocalBrowserKeyDb();
  try{await new Promise((resolve,reject)=>{
    const tx=db.transaction('keys','readwrite');
    tx.objectStore('keys').delete('admin');
    tx.oncomplete=()=>resolve();
    tx.onerror=()=>reject(tx.error);
    tx.onabort=()=>reject(tx.error);
  })}finally{db.close()}
}
const localBrowserBase64=buffer=>btoa(String.fromCharCode(...new Uint8Array(buffer)));
async function rememberLocalBrowser(signal){
  const pair=await crypto.subtle.generateKey({name:'ECDSA',namedCurve:'P-256'},false,['sign','verify']);
  const publicKey=localBrowserBase64(await crypto.subtle.exportKey('spki',pair.publicKey));
  const registered=await api('/api/auth/browser-key',{method:'POST',body:JSON.stringify({publicKey}),signal});
  if(!registered?.id)throw new Error('Agent did not accept the browser signing key.');
  await localBrowserKeyWrite({id:registered.id,privateKey:pair.privateKey});
}

let LOGIN_PENDING=false;
function normalizeLoginUrl(value){
  let url;
  try{url=new URL(value.trim()||location.origin)}catch{throw new Error('Enter a valid HTTP or HTTPS Agent URL.')}
  if(!['http:','https:'].includes(url.protocol)||url.username||url.password||url.search||url.hash)
    throw new Error('Enter a valid HTTP or HTTPS Agent URL without credentials, query or fragment.');
  if(location.protocol==='https:'&&url.protocol==='http:'&&!['localhost','127.0.0.1','[::1]'].includes(url.hostname))
    throw new Error('An HTTPS dashboard cannot connect to an insecure HTTP Agent. Use HTTPS instead.');
  return url.href.replace(/\/+$/,'');
}
function updateLoginTarget(){
  const input=$('#loginUrl'),target=$('#loginTarget'),remember=$('#rememberBrowserField');
  let local=false,httpWarning=false;
  try{
    const address=normalizeLoginUrl(input.value);
    const url=new URL(address);
    target.textContent=address;
    local=address===location.origin&&isLocalBrowserOrigin()&&!window.GSC_CLOUD_MODE;
    httpWarning=url.protocol==='http:'&&!['localhost','127.0.0.1','[::1]'].includes(url.hostname);
  }catch{target.textContent='Enter a valid Agent URL';globalThis.GSC_I18N?.set(target,'Enter a valid Agent URL');}
  remember.classList.toggle('hidden',!local);
  if(!local)$('#loginRememberBrowser').checked=false;
  $('#loginTransportWarning').classList.toggle('hidden',!httpWarning);
}
function showLoginError(message=''){
  const error=$('#loginError');error.textContent=message;
  globalThis.GSC_I18N?.set(error,message);
  error.classList.toggle('hidden',!message);
}
function showLoginForm(message=''){
  $('#loginLoading').classList.add('hidden');
  $('#loginForm').classList.remove('hidden');
  updateLoginTarget();
  showLoginError(message);
}
function setLoginPending(pending){
  LOGIN_PENDING=pending;
  const form=$('#loginForm'),button=$('#loginConnectBtn');
  form.setAttribute('aria-busy',String(pending));
  for(const input of form.querySelectorAll('input,button'))input.disabled=pending;
  button.textContent=pending?'Signing in…':'Sign in';
  globalThis.GSC_I18N?.set(button,pending?'Signing in…':'Sign in');
}
async function verifyLoginIdentity(connection,signal){
  // No redirect may forward a bearer to a different endpoint. Authenticate once,
  // rather than fetching health followed by a second identity request.
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),12000);
  const cancel=()=>controller.abort();
  if(signal?.aborted)cancel();
  else signal?.addEventListener('abort',cancel,{once:true});
  try{
    const response=await fetch(connection.url+'/api/auth/me',{
      headers:{Authorization:'Bearer '+connection.token},cache:'no-store',redirect:'error',signal:controller.signal
    });
    if(response.status===401||response.status===403)throw new Error('The API token was rejected. Check the token and Agent address.');
    if(response.status===429)throw new Error('Too many attempts. Wait a moment before trying again.');
    if(!response.ok)throw new Error('The Agent could not sign you in. Check the service status and try again.');
    let identity;
    try{identity=await response.json()}catch{throw new Error('This address did not return a valid Agent identity.')}
    if(!identity||!['admin','readonly'].includes(String(identity.role).toLowerCase()))
      throw new Error('This address did not return a valid Agent identity.');
    return identity;
  }catch(e){
    if(e.name==='AbortError')throw new Error('Connection timed out. Check the Agent address and service status.');
    if(e instanceof TypeError)throw new Error('Cannot reach the Agent. Check the address, network and HTTPS settings.');
    throw e;
  }finally{
    clearTimeout(timeout);signal?.removeEventListener('abort',cancel);
  }
}
function persistLoginConnection(connection){
  CFG=connection;
  try{sessionStorage.setItem(CFG_KEY,JSON.stringify(CFG))}catch{}
}
function clearLoginConnection(){
  CFG={url:'',token:''};
  try{sessionStorage.removeItem(CFG_KEY)}catch{}
}
async function login(){
  if(LOGIN_PENDING)return;
  const token=$('#loginToken').value.trim();
  let url;
  showLoginError();
  try{url=normalizeLoginUrl($('#loginUrl').value)}catch(e){$('#loginAdvanced').open=true;showLoginError(e.message);$('#loginUrl').focus();return}
  if(!token){showLoginError('Enter your API token.');$('#loginToken').focus();return}
  const remember=url===location.origin&&isLocalBrowserOrigin()&&$('#loginRememberBrowser').checked;
  setLoginPending(true);
  let rememberWarning=false;
  try{
    const connection={url,token};
    const identity=await verifyLoginIdentity(connection);
    persistLoginConnection(connection);
    if(remember&&String(identity.role).toLowerCase()==='admin'){
      const rememberController=new AbortController();
      const rememberTimeout=setTimeout(()=>rememberController.abort(),5000);
      try{await rememberLocalBrowser(rememberController.signal)}catch{rememberWarning=true}
      finally{clearTimeout(rememberTimeout)}
    }else if(remember){rememberWarning=true}
    const tokenInput=$('#loginToken');tokenInput.type='password';tokenInput.value='';
    await boot(identity);
    if(rememberWarning)toast('Signed in, but this browser could not be remembered. Local Admin access is required.','err');
    if(new URLSearchParams(location.search).has("manageTokens"))openTokens();
  }catch(e){
    clearLoginConnection();
    showLoginError(e.message||'Sign-in failed. Try again.');
  }finally{setLoginPending(false)}
}
async function logout(){
  if(isLocalBrowserOrigin()){
    try{
      const saved=await localBrowserKeyRead();
      if(saved?.id)await fetch('/auth/browser-key/forget',{method:'POST',cache:'no-store',
        headers:{'Content-Type':'application/json'},body:JSON.stringify({id:saved.id})});
    }catch{}
    try{await localBrowserKeyDelete()}catch{}
  }
  clearLoginConnection();
  location.reload();
}

async function boot(){
  $('#login').classList.add('hidden');$('#app').classList.remove('hidden');$('#agentLabel').textContent='· '+CFG.url;
  await loadCapabilities();
  try{await loadProfiles()}catch(e){toast('Game profiles failed: '+e.message,'err')}
  await refreshAll();
  if(location.hash==='#settings')history.replaceState(null,'','#system');
  if(location.hash==='#system'||location.hash==='#installs')showView(location.hash.slice(1));
  await checkForUpdate(false);
  setInterval(()=>{loadServers();refreshJobs(false)},5000);
  setInterval(()=>refreshJobs(false),2000);
  setInterval(()=>checkForUpdate(false),30*60*1000);
}

function isLocalBrowserOrigin(){
  return ['localhost','127.0.0.1','[::1]','::1'].includes(location.hostname.toLowerCase());
}

async function restoreBrowserLogin(){
  if(!isLocalBrowserOrigin()||window.GSC_CLOUD_MODE)return false;
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),12000);
  try{
    const stored=await localBrowserKeyRead();
    if(!stored?.id||!stored.privateKey)return false;
    const headers={'Content-Type':'application/json'};
    const challengeResponse=await fetch('/auth/browser-key/challenge',{method:'POST',
      cache:'no-store',headers,signal:controller.signal,body:JSON.stringify({id:stored.id})});
    if(challengeResponse.status===204){await localBrowserKeyDelete();return false}
    if(!challengeResponse.ok)return false;
    const {challenge}=await challengeResponse.json();
    if(typeof challenge!=='string')return false;
    const payload=new TextEncoder().encode('gsc-browser-v1:'+stored.id+':'+challenge);
    const signature=localBrowserBase64(await crypto.subtle.sign(
      {name:'ECDSA',hash:'SHA-256'},stored.privateKey,payload));
    const exchange=await fetch('/auth/browser-key/redeem',{method:'POST',cache:'no-store',
      headers,signal:controller.signal,body:JSON.stringify({id:stored.id,challenge,signature})});
    if(!exchange.ok)return false;
    const result=await exchange.json();
    if(typeof result.token!=='string'||!result.token)return false;
    const connection={url:location.origin,token:result.token};
    const identity=await verifyLoginIdentity(connection,controller.signal);
    persistLoginConnection(connection);
    await boot(identity);
    return true;
  }catch{clearLoginConnection();return false}
  finally{clearTimeout(timeout)}
}

async function init(){
  try{localStorage.removeItem(CFG_KEY)}catch{}
  const params=new URLSearchParams(location.search);
  const token=params.get('token'),url=params.get('url');
  // Scrub legacy deep-link credentials before any request or persistence.
  if(token)history.replaceState({},'',location.pathname+location.hash);
  if(window.GSC_CLOUD_MODE){
    $('#loginHeading').textContent='Cloudflare Access';
    $('#loginDescription').textContent='Opening your protected remote workspace.';
    globalThis.GSC_I18N?.set($('#loginDescription'),'Opening your protected remote workspace.');
    $('#loginFootnote').textContent='Authentication is managed by Cloudflare Access.';
    globalThis.GSC_I18N?.set($('#loginFootnote'),'Authentication is managed by Cloudflare Access.');
    try{
      const connection={url:location.origin,token:'cloud-gateway'};
      const identity=await verifyLoginIdentity(connection);
      persistLoginConnection(connection);
      await boot(identity);
      return;
    }catch{
      clearLoginConnection();
      $('#loginLoading').classList.add('hidden');
      $('#cloudLoginState').classList.remove('hidden');
      return;
    }
  }
  let previous=null;
  if(token){
    // Legacy links can authenticate only to the page's own Agent, never a
    // different destination supplied by a query parameter.
    try{if(normalizeLoginUrl(url||location.origin)===location.origin)previous={url:location.origin,token}}catch{}
  }
  else{try{previous=JSON.parse(sessionStorage.getItem(CFG_KEY)||'null')}catch{}}
  let message='';
  if(previous?.url&&previous?.token){
    try{
      const connection={url:normalizeLoginUrl(previous.url),token:previous.token};
      const identity=await verifyLoginIdentity(connection);
      persistLoginConnection(connection);
      await boot(identity);
      if(params.has("manageTokens"))openTokens();
      return;
    }catch(e){message=e.message;clearLoginConnection()}
  }
  if(await restoreBrowserLogin()){if(params.has("manageTokens"))openTokens();return}
  $('#loginUrl').value=location.origin;
  showLoginForm(message);
}
window.addEventListener('DOMContentLoaded',init);

async function refreshAll(){await Promise.allSettled([loadServers(),refreshJobs(false),...(ACTIVE_HOST_ID==='all'?[]:[checkForUpdate(false)])])}
async function loadServers(){try{const[s,st]=await Promise.all([api('/api/servers'),api('/api/status')]);SERVERS=s||[];STATUS={};for(const x of(st||[]))STATUS[x.id]=x;if(CURRENT_VIEW==='servers')renderServers();else if(CURRENT_VIEW==='system')renderSystemView()}catch(e){toast('Refresh failed: '+e.message,'err')}}
function profileFor(d){return PROFILES.find(g=>g.key===d.gameProfileKey)||PROFILES.find(g=>g.appId&&g.appId===d.steamAppId)||PROFILES.find(g=>g.gameType===d.gameType&&g.gameType!=='SteamGeneric')||null}
function serverMeta(d){const g=profileFor(d);return g?{icon:g.icon,label:g.label,profile:g}:{icon:'🖥',label:d.gameType||'Server',profile:{caps:{}}}}

function renderServers(){
  const root=$('#servers');
  if(!SERVERS.length){root.innerHTML=`<div class="empty">No servers registered yet.<br><br><button class="btn primary" ${uiAction('openCreate')} data-gsc-i18n="Create your first server">Create your first server</button> <button class="btn" ${uiAction('openDiscover')} data-gsc-i18n="Discover installed servers">Discover installed servers</button> <button class="btn" ${uiAction('openAddExisting')} data-gsc-i18n="Add existing server">Add existing server</button></div>`;return}
  root.innerHTML='<div class="grid">'+SERVERS.map(d=>{
    const s=STATUS[d.id]||{},m=serverMeta(d),up=s.processState==='Running',busy=['Starting','Stopping'].includes(s.processState),caps=m.profile.caps||{};
    return `<div class="card"><div class="cardhead"><div class="icon">${m.icon}</div><div class="grow"><div class="title">${esc(d.name)}</div><div class="sub">${esc(m.label)} · ${d.hostingMode==='Vm'?'Hyper-V':'This machine'} · ${esc(d.guestWorkingDir||'')}</div></div><div class="state ${up?'up':busy?'warn':'down'}">${esc(s.processState||'Unknown')}</div></div><div class="metrics"><span>PID <b>${s.pidInGuest??'—'}</b></span><span>CPU <b>${s.cpuPercent!=null?Number(s.cpuPercent).toFixed(1)+'%':'—'}</b></span><span>RAM <b>${s.memoryMB!=null?s.memoryMB+' MB':'—'}</b></span>${caps.rcon&&d.rconPort?`<span>RCON <b>${esc(d.rconHost||'auto')}:${d.rconPort}</b></span>`:''}</div>${s.lastError?`<div class="callout">${esc(s.lastError)}</div>`:''}<div class="actions"><button class="btn" ${up||busy?'disabled':''} ${uiAction('action',[d.id,'start'])} data-gsc-i18n="Start">Start</button><button class="btn danger" ${!up||busy?'disabled':''} ${uiAction('action',[d.id,'stop'])} data-gsc-i18n="Stop">Stop</button><button class="btn" ${!up||busy?'disabled':''} ${uiAction('action',[d.id,'restart'])} data-gsc-i18n="Restart">Restart</button><button class="btn primary" ${uiAction('openManage',[d.id])} data-gsc-i18n="Manage">Manage</button></div></div>`
  }).join('')+'</div>';
}

async function action(id,a){toast(`${id}: ${a}…`);try{const r=await api(`/api/servers/${encodeURIComponent(id)}/${a}`,{method:'POST'});toast(r.success?r.message:('Failed: '+r.message),r.success?'ok':'err');setTimeout(loadServers,800)}catch(e){toast(`${a} failed: ${e.message}`,'err')}}

async function checkForUpdate(force=false){
  if(!CFG.token)return null;
  try{
    UPDATE_STATUS=await api(force?'/api/version/check':'/api/version',force?{method:'POST'}:{});
    const btn=$('#updateBtn');
    if(btn){btn.classList.toggle('hidden',!UPDATE_STATUS?.updateAvailable);btn.textContent=UPDATE_STATUS?.updateAvailable?`Update · ${UPDATE_STATUS.latestVersion}`:'Update'}
    if(CURRENT_VIEW==='system')renderSystemView();
    return UPDATE_STATUS;
  }catch{return UPDATE_STATUS}
}

async function openUpdate(){
  const s=await checkForUpdate(true);
  if(!s)return toast('Could not check for updates.','err');
  const available=!!s.updateAvailable;
  const body=`<div class="kv"><div data-gsc-i18n="Running">Running</div><div class="mono">${esc(s.currentVersion||'unknown')}</div><div data-gsc-i18n="Latest">Latest</div><div class="mono">${esc(s.latestVersion||'unknown')}</div><div data-gsc-i18n="Checked">Checked</div><div>${s.checkedAt?new Date(s.checkedAt).toLocaleString():'—'}</div></div>${available?'<div class="callout info" data-gsc-i18n="The Agent will restart briefly. Server processes are not stopped.">The Agent will restart briefly. Server processes are not stopped.</div>':'<div class="callout info" data-gsc-i18n="This Agent is up to date.">This Agent is up to date.</div>'}`;
  const foot=`<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Close">Close</button>${available?`<button class="btn primary" ${uiAction('startSelfUpdate')} data-gsc-i18n="Install update">Install update</button>`:''}`;
  openModal('GameServerControl update',body,foot);
}

async function startSelfUpdate(){
  if(!confirm('Update GameServerControl now? The control panel will be unavailable briefly while the Agent restarts. Game servers keep running.'))return;
  $('#modalBody').innerHTML='<div class="empty"><b data-gsc-i18n="Starting updater…">Starting updater…</b><br><br>The page will reconnect automatically after the Agent restarts.</div>';$('#modalFoot').innerHTML='';
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
  $('#modalBody').innerHTML='<div class="callout" data-gsc-i18n="The Agent did not return within three minutes. Check the service and installer log.">The Agent did not return within three minutes. Check the service and installer log.</div>';
  $('#modalFoot').innerHTML=`<button class="btn" ${uiAction('reloadPage')} data-gsc-i18n="Reload">Reload</button>`;
}

function reloadPage(){location.reload()}

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
    ? '<div class="callout info" data-gsc-i18n="Hyper-V has its own guest network stack. GameServerControl does not change the guest Windows Firewall from the host.">Hyper-V has its own guest network stack. GameServerControl does not change the guest Windows Firewall from the host.</div>'
    : !looksWindows
      ? '<div class="callout info" data-gsc-i18n="Automatic program firewall rules apply to Windows BareMetal servers only.">Automatic program firewall rules apply to Windows BareMetal servers only.</div>'
      : isExe
        ? `<div class="callout info"><b data-gsc-i18n="Windows Firewall:">Windows Firewall:</b> GameServerControl manages persistent inbound and outbound Allow rules for <span class="mono">${esc(d.guestExePath)}</span>. The ports below are currently used for collision detection, not firewall filtering.</div>`
        : '<div class="callout info" data-gsc-i18n="This server launches through a script/JAR rather than an .exe, so a reliable Windows program rule cannot be created yet. Port reservations still prevent accidental conflicts.">This server launches through a script/JAR rather than an .exe, so a reliable Windows program rule cannot be created yet. Port reservations still prevent accidental conflicts.</div>';
  const managedHint=managed?`<div class="callout info"><b>${esc(g.label)} ports are profile-managed.</b> Change them in Configuration. GameServerControl will update the game launch arguments and these reservations together.</div>`:'';
  const defaults=!managed&&(g?.ports||[]).length?`<button class="btn" ${uiAction('resetNetworkDefaults',[id])}>Reset to ${esc(g.label)} defaults</button>`:'';
  const readOnly=managed?' readonly':'';
  const footer=managed
    ? `<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Close">Close</button><button class="btn primary" ${uiActions([['closeModal',[]],['openConfig',[id]]])} data-gsc-i18n="Edit Configuration">Edit Configuration</button>`
    : `<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Cancel">Cancel</button><button class="btn primary" ${uiAction('saveNetwork',[id])} data-gsc-i18n="Save network settings">Save network settings</button>`;
  openModal('Network — '+d.name,`${fw}${managedHint}<div class="section" data-gsc-i18n="Reserved/listening ports">Reserved/listening ports</div><div class="field"><label data-gsc-i18n="One port or range per line">One port or range per line</label><textarea id="networkPorts" class="mono" style="min-height:180px"${readOnly}>${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Format: <span class="mono">UDP 2456-2457 Game</span> or <span class="mono">TCP 7777 Game/API</span>. BareMetal servers on this Agent may not reserve overlapping ports of the same protocol.</div></div>${defaults}<div class="section" data-gsc-i18n="Profile defaults">Profile defaults</div><div class="tiny muted">${(g?.ports||[]).length?(g.ports.map(formatNetworkPort).map(esc).join('<br>')):'No curated port defaults are defined for this game yet.'}</div>`,footer,true)
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
  // Per-server network controls are available under Server → Settings → Network.
  // The legacy Manage modal no longer exists, so no extra network tile is injected.
  if(typeof openCreateSettings==='function'&&!openCreateSettings.__networkWrapped){
    const originalCreate=openCreateSettings;
    const wrappedCreate=function(key){
      originalCreate(key);
      const g=GAMES.find(x=>x.key===key),body=$('#modalBody');
      const path=$('#cPath');
      if(path&&g?.defaultInstallPath){path.value=g.defaultInstallPath;path.dispatchEvent(new Event('input'))}
      if(!body)return;
      if(g?.gameType==='Minecraft'){
        const isBedrock=g.key==='minecraft-bedrock';
        const box=document.createElement('div');
        box.className='formgrid';
        box.innerHTML=`<div class="field"><label data-gsc-i18n="Minecraft version">Minecraft version</label><input id="cMinecraftVersion" placeholder="${isBedrock?'latest official stable':'latest stable (or e.g. 1.21.11)'}" maxlength="32" ${isBedrock?'disabled':''}><div class="help" data-gsc-i18n="Leave empty for the latest release; Paper requires a stable build.">Leave empty for the latest release; Paper requires a stable build.</div></div><div class="field"><label data-gsc-i18n="Game port">Game port</label><input id="cGamePort" type="number" min="1024" max="65535" value="${isBedrock?'19132':'25565'}"></div>${isBedrock?'':`<div class="field"><label data-gsc-i18n="Maximum RAM (MB)">Maximum RAM (MB)</label><input id="cMinecraftMemory" type="number" min="512" max="65536" step="256" value="4096"><div class="help" data-gsc-i18n="Compatible Java must be installed on the computer running the Agent.">Compatible Java must be installed on the computer running the Agent.</div></div>`}<div class="field" style="grid-column:1/-1"><label><input id="cMinecraftLicense" type="checkbox"> I accept the ${isBedrock?'Minecraft Bedrock Dedicated Server license':'Minecraft Java EULA'} for this installation.</label><div class="help" data-gsc-i18n="Installation is disabled until you explicitly accept the official license terms.">Installation is disabled until you explicitly accept the official license terms.</div></div>`;
        body.appendChild(box);
      }else if(g?.key==='valheim'){
        const box=document.createElement('div');box.className='formgrid';box.innerHTML='<div class="field"><label data-gsc-i18n="Game port">Game port</label><input id="cGamePort" type="number" min="1024" max="65534" value="2456"><div class="help" data-gsc-i18n="Valheim reserves this UDP port and the next one (port+1).">Valheim reserves this UDP port and the next one (port+1).</div></div>';body.appendChild(box);
      }else if(g?.key==='dragonwilds'){
        const box=document.createElement('div');box.className='formgrid';box.innerHTML='<div class="field"><label data-gsc-i18n="Game port">Game port</label><input id="cGamePort" type="number" min="1024" max="65535" value="7777"></div><div class="field"><label data-gsc-i18n="Beacon port">Beacon port</label><input id="cBeaconPort" type="number" min="1024" max="65535" value="8888"></div>';body.appendChild(box);
      }else if(g?.ports?.length){
        const box=document.createElement('div');box.className='callout info';box.innerHTML='<b data-gsc-i18n="Default network ports:">Default network ports:</b><br>'+g.ports.map(formatNetworkPort).map(esc).join('<br>')+'<br><span class="tiny muted" data-gsc-i18n="GameServerControl checks these against other BareMetal servers before registration.">GameServerControl checks these against other BareMetal servers before registration.</span>';body.appendChild(box)
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
        sel.innerHTML='<option value="BareMetal" data-gsc-i18n="This machine (BareMetal)">This machine (BareMetal)</option>';sel.value='BareMetal';syncEditVisibility();
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
      let ports;try{ports=readInstallPorts(g);
        if(g.gameType==='Minecraft'){
          const port=Number.parseInt($('#cGamePort')?.value||'0',10);
          if(!Number.isInteger(port)||port<1024||port>65535)throw new Error('Minecraft port must be between 1024 and 65535.');
          ports=[{name:'Game',protocol:g.key==='minecraft-bedrock'?'Udp':'Tcp',port,endPort:null}];
        }
      }catch(e){if(err){err.textContent=e.message;err.classList.remove('hidden')}return}
      const conflict=findNetworkConflict(ports);
      if(conflict){const msg=`Port conflict with ${conflict.server.name}: ${formatNetworkPort(conflict.ours)} overlaps ${formatNetworkPort(conflict.theirs)}.`;if(err){err.textContent=msg;err.classList.remove('hidden')}else toast(msg,'err');return}
      if(g.gameType==='Minecraft'){
        const memoryMb=Number.parseInt($('#cMinecraftMemory')?.value||'4096',10);
        if(!$('#cMinecraftLicense')?.checked){if(err){err.textContent='Accept the Minecraft license terms before installing.';err.classList.remove('hidden')}return}
        if(!Number.isInteger(memoryMb)||memoryMb<512||memoryMb>65536){if(err){err.textContent='RAM must be between 512 and 65536 MB.';err.classList.remove('hidden')}return}
        const isBedrock=g.key==='minecraft-bedrock';
        const req={profileKey:g.key,installPath:path,serverId:id,name,version:$('#cMinecraftVersion')?.value.trim()||null,memoryMb,port:ports[0].port,acceptEula:!isBedrock,acceptBedrockTerms:isBedrock};
        try{const ack=await api('/api/servers/minecraft/install',{method:'POST',body:JSON.stringify(req)});rememberJob({jobId:ack.jobId,name,icon:g.icon,path});closeModal();showView('installs');toast('Minecraft installation queued.','ok');await refreshJobs()}
        catch(e){if(err){err.textContent=e.message;err.classList.remove('hidden')}}
        return;
      }
      const args=applyInstallPorts(g,expandList(g.args,path),ports);
      const def={id,name,vmName:'',gameType:g.gameType||'SteamGeneric',gameProfileKey:g.key,guestExePath:joinPath(path,g.exe),guestWorkingDir:path,startArgs:args,saveDirs:expandList(g.saveRel,path),steamAppId:g.appId,guestCredentialId:null,logPathInGuest:g.logRel?expandRoot(g.logRel,path):null,rconHost:g.caps?.rcon?'auto':null,rconPort:g.caps?.rcon?(g.rconPort||null):null,rconPassword:null,hostingMode:'BareMetal',scheduledTaskName:null,stopProcessNames:null,discordWebhookUrl:null,networkPorts:ports};
      try{const ack=await api('/api/servers/install',{method:'POST',body:JSON.stringify({steamAppId:g.appId,installPath:path,serverDef:def})});rememberJob({jobId:ack.jobId,name,icon:g.icon,path});closeModal();showView('installs');toast(`${g.label} installation started in background.`,'ok');await refreshJobs()}catch(e){if(err){err.textContent=e.message;err.classList.remove('hidden')}else toast(e.message,'err')}
    };
    wrappedStart.__networkWrapped=true;window.startInstall=wrappedStart;
  }
}
window.addEventListener('DOMContentLoaded',installNetworkUiHooks);

/* Control-room overrides externalized from index.html for CSP. */
window.GSCUX={booted:false,selected:null,actions:{},refreshing:false,noticeTimer:null,statusStream:null,statusStreamReady:false,statusStreamGeneration:0,statusStreamRetry:null,lastHttpSync:0};

function clearNotice(){const n=$('#noticeBar');if(n)n.classList.add('hidden');if(GSCUX.noticeTimer){clearTimeout(GSCUX.noticeTimer);GSCUX.noticeTimer=null}}
function setNotice(msg,kind='info',persistent=false,title=null){
  const n=$('#noticeBar'),text=$('#noticeText'),head=$('#noticeTitle');if(!n||!text||!head)return;
  clearNotice();n.className='noticebar '+(kind||'info');text.textContent=msg||'';head.textContent=title||(kind==='err'?'Error':kind==='ok'?'Done':'Status');n.classList.remove('hidden');
  if(!persistent)GSCUX.noticeTimer=setTimeout(clearNotice,kind==='err'?9000:5000);
}
// The notice bar lives inside #app, so it is hidden while the login form is open.
// Keep the floating toast available for login validation and connection errors.
const showLoginToast=toast;
toast=function(msg,kind=''){
  if(!$('#login')?.classList.contains('hidden'))return showLoginToast(msg,kind);
  setNotice(msg,kind==='err'?'err':kind==='ok'?'ok':'info',false);
};

boot=async function(){
  if(GSCUX.booted)return;GSCUX.booted=true;
  $('#login').classList.add('hidden');$('#app').classList.remove('hidden');$('#agentLabel').textContent='· '+CFG.url;
  try{ACTIVE_HOST_ID=sessionStorage.getItem(HOST_KEY)||(window.GSC_CLOUD_MODE?'all':'local');await refreshManagedHosts()}catch(e){ACTIVE_HOST_ID=window.GSC_CLOUD_MODE?'all':'local';setNotice('Host list unavailable: '+e.message,'err',true)}
  if(ACTIVE_HOST_ID!=='all')try{await loadProfiles()}catch(e){setNotice('Game profiles failed: '+e.message,'err',true)}
  await Promise.allSettled([loadServers(),refreshJobs(false)]);
  if(ACTIVE_HOST_ID!=='all')void checkForUpdate(false);
  // The later boot override is the active entry point; preserve legacy Settings links.
  if(location.hash==='#settings')history.replaceState(null,'','#system');
  if(location.hash==='#system'||location.hash==='#installs')showView(location.hash.slice(1));
  startStatusStream();
  // With an active SignalR stream, keep a slow HTTP reconciliation for changes
  // to the registered server list. If disconnected, retain the fast fallback.
  setInterval(()=>{
    if(GSCUX.statusStreamReady && Date.now()-GSCUX.lastHttpSync<60000)return;
    loadServers(true);
  },2000);
  setInterval(()=>{try{const active=loadJobRefs().some(r=>!(JOB_CACHE[r.jobId]?.finished));if(CURRENT_VIEW==='installs'||active)refreshJobs(false)}catch{}},1000);
  setInterval(()=>{if(ACTIVE_HOST_ID!=='all')checkForUpdate(false)},30*60*1000);
};

loadServers=async function(silent=false){
  if(GSCUX.refreshing&&GSCUX.refreshEpoch===HOST_SWITCH_EPOCH)return;
  if(ACTIVE_HOST_ID==='all'&&Date.now()-LAST_HOST_OVERVIEW<12000)return;
  GSCUX.refreshing=true;
  const selected=ACTIVE_HOST_ID,epoch=HOST_SWITCH_EPOCH;
  GSCUX.refreshEpoch=epoch;
  try{
    if(selected==='all'){
      const overview=await api('/api/hosts/overview');
      if(epoch!==HOST_SWITCH_EPOCH)return;
      HOST_OVERVIEW=Array.isArray(overview)?overview:[];
      LAST_HOST_OVERVIEW=Date.now();
      SERVERS=[];STATUS={};
    }else{
      const[s,st]=await Promise.all([api('/api/servers'),api('/api/status')]);
      if(epoch!==HOST_SWITCH_EPOCH)return;
      SERVERS=s||[];STATUS={};for(const x of(st||[]))STATUS[x.id]=x;
      GSCUX.lastHttpSync=Date.now();
      if(GSCUX.selected&&!SERVERS.some(x=>x.id===GSCUX.selected))GSCUX.selected=null;
      if(!GSCUX.selected&&SERVERS.length)GSCUX.selected=SERVERS[0].id;
    }
    renderServers();
  }catch(e){if(!silent)setNotice('Refresh failed: '+e.message,'err',false)}
  finally{if(GSCUX.refreshEpoch===epoch)GSCUX.refreshing=false}
};


function stopStatusStream(){
  GSCUX.statusStreamGeneration++;
  GSCUX.statusStreamReady=false;
  if(GSCUX.statusStreamRetry){clearTimeout(GSCUX.statusStreamRetry);GSCUX.statusStreamRetry=null}
  if(GSCUX.statusStream){GSCUX.statusStream.close();GSCUX.statusStream=null}
}

function startStatusStream(){
  stopStatusStream();
  if(ACTIVE_HOST_ID==='all'||typeof createServerStatusConnection!=='function')return;
  const generation=GSCUX.statusStreamGeneration,host=ACTIVE_HOST_ID;
  const connect=async()=>{
    if(generation!==GSCUX.statusStreamGeneration||host!==ACTIVE_HOST_ID)return;
    try{
      const connection=await createServerStatusConnection(message=>{
        if(generation!==GSCUX.statusStreamGeneration||host!==ACTIVE_HOST_ID||
           message.type!==1||String(message.target||'').toLowerCase()!=='statuschanged')return;
        const status=message.arguments?.[0];
        if(!status||!SERVERS.some(server=>server.id===status.id))return;
        STATUS[status.id]=status;
        if(CURRENT_VIEW==='servers')renderServers();
      },()=>{
        if(generation!==GSCUX.statusStreamGeneration)return;
        GSCUX.statusStreamReady=false;
        GSCUX.statusStream=null;
        GSCUX.statusStreamRetry=setTimeout(connect,5000);
      });
      if(generation!==GSCUX.statusStreamGeneration||host!==ACTIVE_HOST_ID){
        connection.close();return;
      }
      GSCUX.statusStream=connection;
      GSCUX.statusStreamReady=true;
      // Recover server definitions and statuses possibly changed during handshake.
      await loadServers(true);
    }catch{
      if(generation===GSCUX.statusStreamGeneration){
        GSCUX.statusStreamReady=false;
        GSCUX.statusStreamRetry=setTimeout(connect,5000);
      }
    }
  };
  void connect();
}

window.selectServer=function(id){GSCUX.selected=id;renderServers()};
function actionVisual(id,actual){const a=GSCUX.actions[id];return a?.state||actual||'Unknown'}
function stateClass(state){return state==='Running'?'up':['Starting','Stopping','Restarting','Working'].includes(state)?'warn':'down'}
function quickTile(icon,title,desc,actionName,args=[]){return `<button class="manage-tile" ${uiAction(actionName,args)}><span class="manage-icon">${icon}</span><span><b>${esc(title)}</b><small>${esc(desc)}</small></span></button>`}

renderServers=function(){
  const root=$('#servers');if(!root)return;
  if(!SERVERS.length){root.innerHTML=`<div class="empty">No servers registered yet.<br><br><button class="btn primary" ${uiAction('openCreate')} data-gsc-i18n="Create your first server">Create your first server</button> <button class="btn" ${uiAction('openDiscover')} data-gsc-i18n="Discover installed servers">Discover installed servers</button> <button class="btn" ${uiAction('openAddExisting')} data-gsc-i18n="Add existing server">Add existing server</button></div>`;return}
  if(!GSCUX.selected||!SERVERS.some(x=>x.id===GSCUX.selected))GSCUX.selected=SERVERS[0].id;
  const rows=SERVERS.map(d=>{const s=STATUS[d.id]||{},m=serverMeta(d),state=actionVisual(d.id,s.processState),selected=d.id===GSCUX.selected;return `<button class="server-row ${selected?'selected':''}" ${uiAction('selectServer',[d.id])}><span class="server-row-icon">${m.icon}</span><span class="server-row-main"><span class="server-row-name">${esc(d.name)}</span><span class="server-row-meta">${esc(m.label)} · ${s.pidInGuest?'PID '+s.pidInGuest:(d.hostingMode==='Vm'?'Hyper-V':'This machine')}</span></span><span class="server-row-state ${stateClass(state)}">${esc(state)}</span></button>`}).join('');
  root.innerHTML=`<div class="server-control-layout"><aside class="server-list">${rows}</aside><div id="serverControlPanel"></div></div>`;
  renderServerControlPanel();
};

window.renderServerControlPanel=function(){
  const host=$('#serverControlPanel'),d=SERVERS.find(x=>x.id===GSCUX.selected);if(!host||!d)return;
  const s=STATUS[d.id]||{},m=serverMeta(d),g=m.profile||{},caps=g.caps||{},pending=GSCUX.actions[d.id],actual=s.processState||'Unknown',display=actionVisual(d.id,actual),up=actual==='Running',busy=!!pending||['Starting','Stopping'].includes(actual);
  let op='';if(pending){op=`<div class="operation-banner ${pending.error?'err':pending.done?'ok':''}">${pending.done||pending.error?'':'<span class="spinner"></span>'}<div><b>${esc(pending.label)}</b>${pending.detail?`<div class="tiny muted">${esc(pending.detail)}</div>`:''}</div></div>`}
  const consoleTile=caps.console==='rcon'?(d.rconPort&&d.rconPassword?quickTile('⌨','Console','Interactive RCON console','openConsole',[d.id]):quickTile('⌨','Set up console','RCON port/password required','openEdit',[d.id])):caps.console==='log'&&d.logPathInGuest?quickTile('▤','Live log','Follow server output live','openLog',[d.id]):'';
  const configTile=caps.config?quickTile('⚙','Configuration','Game-specific settings','openConfig',[d.id]):'';
  const modsTile=caps.mods?quickTile('◇','Mods','Browse, install and update','openMods',[d.id]):'';
  const network=(typeof effectiveNetworkPorts==='function'?effectiveNetworkPorts(d):d.networkPorts||[]).map(formatNetworkPort).join(', ');
  host.innerHTML=`<div class="server-detail-panel"><div class="server-detail-head"><div class="server-detail-icon">${m.icon}</div><div class="grow"><div class="server-detail-title">${esc(d.name)}</div><div class="server-detail-sub">${esc(m.label)} · ${d.hostingMode==='Vm'?'Hyper-V virtual machine':'This machine'}</div></div><div class="server-state-large ${stateClass(display)}">${esc(display)}</div></div><div class="server-detail-body">${op}${s.lastError?`<div class="callout">${esc(s.lastError)}</div>`:''}<div class="control-buttons"><button class="btn primary control-btn" ${up||busy?'disabled':''} ${uiAction('action',[d.id,'start'])} data-gsc-i18n="Start">Start</button><button class="btn danger control-btn stop" ${!up||busy?'disabled':''} ${uiAction('action',[d.id,'stop'])} data-gsc-i18n="Stop">Stop</button><button class="btn control-btn restart" ${!up||busy?'disabled':''} ${uiAction('action',[d.id,'restart'])} data-gsc-i18n="Restart">Restart</button><button class="btn control-btn" ${up||busy||!d.steamAppId?'disabled':''} title="${up?'Stop the server before updating':!d.steamAppId?'No Steam App ID configured':'Update and validate game files'}" ${uiAction('action',[d.id,'update'])} data-gsc-i18n="Update">Update</button></div><div class="detail-metrics"><div class="metric-box"><span>PID</span><b>${s.pidInGuest??'—'}</b></div><div class="metric-box"><span>CPU</span><b>${s.cpuPercent!=null?Number(s.cpuPercent).toFixed(1)+'%':'—'}</b></div><div class="metric-box"><span>RAM</span><b>${s.memoryMB!=null?s.memoryMB+' MB':'—'}</b></div><div class="metric-box"><span data-gsc-i18n="Last backup">Last backup</span><b>${s.lastBackupAt?new Date(s.lastBackupAt).toLocaleString():'—'}</b></div></div><div class="quick-panel">${consoleTile}${configTile}${modsTile}${quickTile('▣','Backups','Create and restore saves','openBackups',[d.id])}${quickTile('◎','Network','Ports and Windows Firewall','openNetwork',[d.id])}${quickTile('⋯','More settings','Schedule, update, edit, delete…','openManage',[d.id])}</div><div class="server-path"><div><b data-gsc-i18n="Folder:">Folder:</b> <span class="mono">${esc(d.guestWorkingDir||'')}</span></div>${network?`<div style="margin-top:4px"><b data-gsc-i18n="Ports:">Ports:</b> ${esc(network)}</div>`:''}</div></div></div>`;
};

action=async function(id,a){
  if(GSCUX.actions[id])return;
  const labels={start:['Starting server…','Starting'],stop:['Stopping server…','Stopping'],restart:['Restarting server…','Restarting'],backup:['Creating backup…','Working'],update:['Updating server…','Working'],apply:['Applying configuration…','Working']};
  const spec=labels[a]||['Working…','Working'];GSCUX.selected=id;GSCUX.actions[id]={kind:a,label:spec[0],state:spec[1],detail:a==='stop'?'Waiting for the server process to exit.':a==='restart'?'Stopping first, then starting again.':''};renderServers();setNotice(spec[0],'info',true);
  try{
    const r=await api(`/api/servers/${encodeURIComponent(id)}/${a}`,{method:'POST'});
    if(!r?.success)throw new Error(r?.message||`${a} failed`);
    GSCUX.actions[id]={kind:a,label:r.message||'Completed',state:a==='stop'?'NotRunning':a==='start'||a==='restart'?'Running':'Working',done:true};
    await loadServers(true);setNotice(r.message||`${a} completed`,'ok',false);
    setTimeout(()=>{delete GSCUX.actions[id];renderServers()},1200);
  }catch(e){GSCUX.actions[id]={kind:a,label:`${a} failed`,state:STATUS[id]?.processState||'Unknown',error:true,detail:e.message};renderServers();setNotice(`${a} failed: ${e.message}`,'err',false);setTimeout(()=>{delete GSCUX.actions[id];renderServers()},5000)}
};

function setWorkspaceHeading(title, context='CONTROL PANEL / OVERVIEW',translateTitle=true){
  const heading=$('#pageHeading'), eyebrow=$('#pageEyebrow');
  if(heading&&heading.textContent!==title)heading.textContent=title;
  if(eyebrow&&eyebrow.textContent!==context)eyebrow.textContent=context;
  if(translateTitle)globalThis.GSC_I18N?.set(heading,title);
  else heading?.removeAttribute('data-gsc-i18n');
  globalThis.GSC_I18N?.set(eyebrow,context);
}
function closeMobileNavigation(){
  $('#app')?.classList.remove('nav-open');
  $('#navScrim')?.classList.add('hidden');
  $('#mobileNavToggle')?.setAttribute('aria-expanded','false');
}
function toggleMobileNavigation(){
  const open=!$('#app')?.classList.contains('nav-open');
  $('#app')?.classList.toggle('nav-open',open);
  $('#navScrim')?.classList.toggle('hidden',!open);
  $('#mobileNavToggle')?.setAttribute('aria-expanded',String(open));
}

function bindStaticUi(){
  const bind=(selector,event,handler)=>{const el=$(selector);if(el)el.addEventListener(event,handler)};
  bind('#mobileNavToggle','click',toggleMobileNavigation);
  bind('#navScrim','click',closeMobileNavigation);
  document.querySelectorAll('.sidebar-nav').forEach(nav=>nav.addEventListener('click',event=>{
    if(event.target.closest('.tab'))closeMobileNavigation();
  }));
  document.addEventListener('keydown',event=>{
    if(event.key==='Escape')closeMobileNavigation();
  });
  bind('#hostSwitch','change',()=>selectHost($('#hostSwitch')?.value||'local'));
  bind('#manageHostsBtn','click',openManagedHosts);
  bind('#loginForm','submit',event=>{event.preventDefault();void login()});
  bind('#loginUrl','input',updateLoginTarget);
  bind('#loginToken','input',()=>showLoginError());
  bind('#cloudRetryBtn','click',()=>location.reload());
  bind('#updateBtn','click',()=>openUpdate());
  bind('#checkUpdateBtn','click',()=>openUpdate());
  bind('#createServerBtn','click',()=>openAddServerMenu());
  bind('#discoverBtn','click',()=>openDiscover());
  bind('#addExistingBtn','click',()=>openAddExisting());
  bind('#tokensBtn','click',()=>openTokens());
  bind('#refreshBtn','click',()=>refreshAll());
  bind('#logoutBtn','click',()=>logout());
  bind('#tabServers','click',()=>showView('servers'));
  bind('#tabInstalls','click',()=>showView('installs'));
  bind('#tabSystem','click',()=>showView('system'));
  bind('#noticeCloseBtn','click',()=>clearNotice());
  bind('#clearFinishedJobsBtn','click',()=>clearFinishedJobs());
  bind('#modalCloseBtn','click',()=>closeModal());
  bind('#overlay','click',event=>{if(event.target===event.currentTarget)closeModal()});
}
bindStaticUi();
