let STAT_POINTS=[];

function discoveredDef(x){
  const g=GAMES.find(v=>v.key===x.presetKey)||GAMES.find(v=>v.appId===x.steamAppId);
  const path=x.installPath.replace(/[\\/]+$/,'');
  const base=slugify(x.displayName)||('server-'+x.steamAppId);
  let id=base,n=2; while(SERVERS.some(s=>s.id===id)) id=base+'-'+n++;
  return {id,name:x.displayName,vmName:'',gameType:'SteamGeneric',guestExePath:x.exePath,guestWorkingDir:path,startArgs:g?.args||[],saveDirs:(g?.saveRel||[]).map(r=>joinPath(path,r)),steamAppId:x.steamAppId,guestCredentialId:'default',logPathInGuest:g?.logRel?joinPath(path,g.logRel):null,rconHost:'auto',rconPort:null,rconPassword:null,hostingMode:'BareMetal',scheduledTaskName:null,stopProcessNames:null,discordWebhookUrl:null};
}

async function openDiscover(){
  openModal('Discover installed servers','<div class="muted">Scanning Steam libraries and known game-server locations…</div>','',true);
  try{
    const r=await api('/api/discover');
    const list=r.servers||[];
    const libs=(r.librariesScanned||[]).map(x=>`<span class="pill">${esc(x)}</span>`).join('');
    $('#modalBody').innerHTML=`<div class="summarybar"><div class="muted tiny">Scanned ${list.length} detected install${list.length===1?'':'s'}</div></div>${libs?`<div class="pills" style="margin-bottom:14px">${libs}</div>`:''}${list.length?list.map((x,i)=>`<div class="job"><div class="jobtop"><div class="grow"><div class="jobname">${esc(x.displayName)}</div><div class="jobphase">${esc(x.installPath)} · App ${esc(x.steamAppId)} · ${esc(x.source)}</div></div><span class="state ${x.alreadyConfigured?'up':'warn'}">${x.alreadyConfigured?'ADDED':'FOUND'}</span></div><div class="jobbuttons">${x.alreadyConfigured?'<button class="btn" disabled>Already configured</button>':`<button class="btn primary" onclick="addDiscovered(${i})">Add to dashboard</button>`}</div></div>`).join(''):'<div class="empty">No supported dedicated-server installs were detected.</div>';
    window.__discover=list;
  }catch(e){$('#modalBody').innerHTML=`<div class="empty">${esc(e.message)}</div>`}
}

async function addDiscovered(i){
  const x=(window.__discover||[])[i]; if(!x)return;
  const def=discoveredDef(x);
  try{await api('/api/servers',{method:'POST',body:JSON.stringify(def)});toast('Added '+def.name+'.','ok');await loadServers();await openDiscover()}
  catch(e){toast('Add failed: '+e.message,'err')}
}

function openAddExisting(){
  openModal('Add existing server',`<div class="callout info">Registers an already-installed server. No game files are downloaded or moved.</div><div class="formgrid"><div class="field"><label>ID</label><input id="aId" placeholder="my-server"></div><div class="field"><label>Name</label><input id="aName" placeholder="My Server"></div><div class="field"><label>Game type</label><select id="aGame"><option>SteamGeneric</option><option>Windrose</option><option>Minecraft</option><option>Custom</option></select></div><div class="field"><label>Steam App ID</label><input id="aSteam"></div></div><div class="field"><label>Server EXE</label><input id="aExe" class="mono" placeholder="C:\\GameServers\\Example\\server.exe"></div><div class="field"><label>Working directory</label><input id="aWork" class="mono" placeholder="C:\\GameServers\\Example"></div><div class="formgrid"><div class="field"><label>Start args — one per line</label><textarea id="aArgs" class="mono"></textarea></div><div class="field"><label>Save dirs — one per line</label><textarea id="aSaves" class="mono"></textarea></div></div><div class="formgrid"><div class="field"><label>Log path</label><input id="aLog" class="mono"></div><div class="field"><label>Scheduled task name</label><input id="aTask"></div></div><div class="section">RCON (optional)</div><div class="formgrid three"><div class="field"><label>Host</label><input id="aRhost" value="auto"></div><div class="field"><label>Port</label><input id="aRport" type="number" min="1" max="65535"></div><div class="field"><label>Password</label><input id="aRpass" type="password"></div></div>`,`<button class="btn" onclick="closeModal()">Cancel</button><button class="btn primary" onclick="saveAddExisting()">Add server</button>`,true);
}

async function saveAddExisting(){
  const id=$('#aId').value.trim(),name=$('#aName').value.trim(),exe=$('#aExe').value.trim(),work=$('#aWork').value.trim();
  if(!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id)||!name||!exe||!work)return toast('Valid ID, name, EXE and working directory are required.','err');
  const def={id,name,vmName:'',gameType:$('#aGame').value,guestExePath:exe,guestWorkingDir:work,startArgs:splitLines($('#aArgs').value),saveDirs:splitLines($('#aSaves').value),steamAppId:$('#aSteam').value.trim()||null,guestCredentialId:'default',logPathInGuest:$('#aLog').value.trim()||null,rconHost:$('#aRhost').value.trim()||null,rconPort:intOrNull($('#aRport').value),rconPassword:$('#aRpass').value||null,hostingMode:'BareMetal',scheduledTaskName:$('#aTask').value.trim()||null,stopProcessNames:null,discordWebhookUrl:null};
  try{await api('/api/servers',{method:'POST',body:JSON.stringify(def)});toast('Server added.','ok');closeModal();await loadServers()}catch(e){toast('Add failed: '+e.message,'err')}
}

function statPath(values,max,w=700,h=150){
  if(values.length<2)return '';
  const m=Math.max(max,...values.map(v=>Number(v)||0),1);
  return values.map((v,i)=>`${i?'L':'M'} ${(i/(Math.max(1,values.length-1))*w).toFixed(1)} ${(h-(Number(v)||0)/m*h).toFixed(1)}`).join(' ');
}
function drawStats(){
  const el=$('#statsChart');if(!el)return;
  const cpu=STAT_POINTS.map(x=>x.cpu||0),ram=STAT_POINTS.map(x=>x.ram||0),last=STAT_POINTS.at(-1)||{};
  const ramMax=Math.max(1024,...ram,1);
  el.innerHTML=`<div class="metrics"><span>CPU <b>${last.cpu!=null?last.cpu.toFixed(1)+'%':'—'}</b></span><span>RAM <b>${last.ram!=null?last.ram+' MB':'—'}</b></span><span>PID <b>${last.pid??'—'}</b></span></div><div class="chartTitle">CPU % · last ${STAT_POINTS.length} samples</div><svg class="chart" viewBox="0 0 700 150" preserveAspectRatio="none"><path class="chartGrid" d="M0 75H700 M0 149H700"/><path class="chartCpu" d="${statPath(cpu,100)}"/></svg><div class="chartTitle">RAM MB · scale ${ramMax} MB</div><svg class="chart" viewBox="0 0 700 150" preserveAspectRatio="none"><path class="chartGrid" d="M0 75H700 M0 149H700"/><path class="chartRam" d="${statPath(ram,ramMax)}"/></svg>`;
}
async function sampleStats(id){
  try{const s=await api(`/api/servers/${encodeURIComponent(id)}/status`);STAT_POINTS.push({at:Date.now(),cpu:s.cpuPercent??0,ram:s.memoryMB??0,pid:s.pidInGuest});if(STAT_POINTS.length>90)STAT_POINTS.shift();drawStats()}catch(e){const el=$('#statsErr');if(el)el.textContent=e.message}
}
async function openStats(id){
  const d=SERVERS.find(x=>x.id===id);STAT_POINTS=[];
  openModal('Stats — '+(d?.name||id),`<div id="statsErr" class="tiny muted"></div><div id="statsChart"><div class="muted">Collecting samples…</div></div>`,`<button class="btn" onclick="closeModal()">Close</button>`,true);
  await sampleStats(id);const timer=setInterval(()=>sampleStats(id),2000);MODAL_CLEANUP=()=>clearInterval(timer);
}

async function fetchLog(id){
  const pane=$('#liveLog');if(!pane)return;
  try{const lines=await api(`/api/servers/${encodeURIComponent(id)}/logs/recent?limit=300`);pane.textContent=(lines||[]).map(x=>`[${new Date(x.at).toLocaleTimeString()}] ${x.text}`).join('\n')||'Waiting for new log lines…';pane.scrollTop=pane.scrollHeight}catch(e){pane.textContent='Log read failed: '+e.message}
}
async function openLog(id){
  const d=SERVERS.find(x=>x.id===id);openModal('Live log — '+(d?.name||id),`<div class="tiny muted" style="margin-bottom:8px">${esc(d?.logPathInGuest||'')}</div><pre id="liveLog" class="log">Starting tail…</pre>`,`<button class="btn" onclick="fetchLog('${jsq(id)}')">Refresh</button><button class="btn" onclick="closeModal()">Close</button>`,true);
  try{await api(`/api/servers/${encodeURIComponent(id)}/logs/tail`,{method:'POST',body:JSON.stringify({action:'start'})})}catch(e){$('#liveLog').textContent='Could not start log tail: '+e.message;return}
  await fetchLog(id);const timer=setInterval(()=>fetchLog(id),1200);MODAL_CLEANUP=()=>{clearInterval(timer);api(`/api/servers/${encodeURIComponent(id)}/logs/tail`,{method:'POST',body:JSON.stringify({action:'stop'})}).catch(()=>{})};
}

async function openTokens(){
  openModal('API tokens','<div id="tokensPane" class="muted">Loading…</div>',`<button class="btn" onclick="closeModal()">Close</button>`,true);await loadTokens();
}
async function loadTokens(){
  const pane=$('#tokensPane');if(!pane)return;
  try{const list=await api('/api/tokens');pane.innerHTML=`<div class="callout info">Tokens authorize Agent API access. Raw token values are shown only once when created.</div><div class="section">Existing tokens</div>${list.length?list.map(t=>`<div class="job"><div class="jobtop"><div class="grow"><div class="jobname">${esc(t.name)}</div><div class="jobphase">${esc(t.id)} · ${esc(t.role)} · ${esc(t.token||'')}</div></div><button class="btn danger" onclick="deleteToken('${jsq(t.id)}')">Delete</button></div></div>`).join(''):'<div class="muted">No secondary tokens.</div>'}<div class="section">Create token</div><div class="formgrid three"><div class="field"><label>ID</label><input id="tokId" placeholder="laptop"></div><div class="field"><label>Name</label><input id="tokName" placeholder="Laptop admin"></div><div class="field"><label>Role</label><select id="tokRole"><option>Admin</option><option>ReadOnly</option></select></div></div><button class="btn primary" onclick="createToken()">Create token</button><div id="newTokenBox"></div>`}catch(e){pane.innerHTML=`<div class="empty">${esc(e.message)}<br><br>This action requires an Admin token.</div>`}
}
async function createToken(){
  const id=$('#tokId').value.trim(),name=$('#tokName').value.trim(),role=$('#tokRole').value;if(!id||!name)return toast('Token ID and name are required.','err');
  try{const r=await api('/api/tokens',{method:'POST',body:JSON.stringify({id,name,role})});const box=$('#newTokenBox');box.innerHTML=`<div class="callout info"><b>Copy this token now — it will not be shown again.</b><div class="consolebar"><input id="rawNewToken" class="input mono" readonly value="${esc(r.token||'')}"><button class="btn" onclick="navigator.clipboard.writeText($('#rawNewToken').value);toast('Token copied.','ok')">Copy</button></div></div>`;await loadTokens();const newBox=$('#newTokenBox');if(newBox)newBox.innerHTML=box.innerHTML;toast('Token created.','ok')}catch(e){toast('Token create failed: '+e.message,'err')}
}
async function deleteToken(id){if(!confirm('Delete API token '+id+'?'))return;try{await api('/api/tokens/'+encodeURIComponent(id),{method:'DELETE'});toast('Token deleted.','ok');await loadTokens()}catch(e){toast('Delete failed: '+e.message,'err')}}
