async function openConfig(id) {
  try {
    const d = SERVERS.find(x => x.id === id);
    const r = await api(`/api/servers/${encodeURIComponent(id)}/config`);
    if (!r.schema) {
      return openModal('Configuration — ' + (d?.name || id), '<div class="empty">No managed configuration is available for this game.</div>');
    }

    const vals = r.values || {};
    let html = '';
    for (const sec of r.schema.sections || []) {
      html += `<div class="section">${esc(sec.title)}</div><div class="formgrid">`;
      for (const f of sec.fields || []) html += configField(f, vals[f.key] ?? f.default ?? '');
      html += '</div>';
    }

    openModal(
      'Configuration — ' + (d?.name || id),
      `<form>${html}</form>`,
      `<button class="btn" ${uiAction('closeModal')}>Cancel</button><button class="btn primary" ${uiAction('saveConfig',[id])}>Save</button>`,
      true
    );
  } catch (e) {
    toast('Configuration failed: ' + e.message, 'err');
  }
}

function configField(f, v) {
  const key = esc(f.key);
  const lab = esc(f.label);
  const desc = f.description ? `<div class="help">${esc(f.description)}</div>` : '';

  if (f.type === 'Toggle') {
    return `<div class="field"><label>${lab}</label><div class="checkrow"><input data-cfg="${key}" type="checkbox" ${String(v).toLowerCase() === 'true' ? 'checked' : ''}><span>Enabled</span></div>${desc}</div>`;
  }
  if (f.type === 'Choice') {
    return `<div class="field"><label>${lab}</label><select data-cfg="${key}">${(f.choices || []).map(x => `<option ${String(x) === String(v) ? 'selected' : ''}>${esc(x)}</option>`).join('')}</select>${desc}</div>`;
  }
  if (f.type === 'Multiline') {
    return `<div class="field"><label>${lab}</label><textarea data-cfg="${key}">${esc(v)}</textarea>${desc}</div>`;
  }

  const type = f.type === 'Password' ? 'password' : (f.type === 'Integer' || f.type === 'Decimal' ? 'number' : 'text');
  return `<div class="field"><label>${lab}</label><input data-cfg="${key}" type="${type}" value="${esc(v)}" ${f.min != null ? `min="${f.min}"` : ''} ${f.max != null ? `max="${f.max}"` : ''} ${f.step != null ? `step="${f.step}"` : ''}>${desc}</div>`;
}

async function saveConfig(id) {
  const vals = {};
  document.querySelectorAll('[data-cfg]').forEach(el => {
    vals[el.dataset.cfg] = el.type === 'checkbox' ? String(el.checked) : String(el.value);
  });

  try {
    await api(`/api/servers/${encodeURIComponent(id)}/config`, { method: 'PUT', body: JSON.stringify(vals) });
    toast('Configuration saved.', 'ok');
    closeModal();
  } catch (e) {
    toast('Save failed: ' + e.message, 'err');
  }
}

async function openBackups(id) {
  try {
    const d = SERVERS.find(x => x.id === id);
    const list = await api(`/api/servers/${encodeURIComponent(id)}/backups`);
    const body = (list || []).length
      ? (list || []).map(b => `<div class="job"><div class="jobname">${esc(b.checkpointName)}</div><div class="jobphase">${new Date(b.createdAt).toLocaleString()} · ${Math.round((b.sizeBytes || 0) / 1024 / 1024)} MB</div><div class="jobbuttons"><button class="btn" ${uiAction('restoreBackup',[id,encodeURIComponent(b.checkpointName)])}>Restore</button><button class="btn danger" ${uiAction('deleteBackup',[id,encodeURIComponent(b.checkpointName)])}>Delete</button></div></div>`).join('')
      : '<div class="empty">No backups yet.</div>';

    openModal(
      'Backups — ' + (d?.name || id),
      body,
      `<button class="btn primary" ${uiActions([['action',[id,'backup']],['closeModal',[]]])}>Backup now</button><button class="btn" ${uiAction('closeModal')}>Close</button>`,
      true
    );
  } catch (e) {
    toast('Backups failed: ' + e.message, 'err');
  }
}

async function restoreBackup(id, name) {
  if (!confirm('Restore this backup? Stop the server first.')) return;
  try {
    const r = await api(`/api/servers/${encodeURIComponent(id)}/backups/${name}/restore`, { method: 'POST' });
    toast(r.success ? r.message : ('Restore failed: ' + r.message), r.success ? 'ok' : 'err');
    closeModal();
  } catch (e) {
    toast('Restore failed: ' + e.message, 'err');
  }
}

async function deleteBackup(id, name) {
  if (!confirm('Delete this backup?')) return;
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/backups/${name}`, { method: 'DELETE' });
    toast('Backup deleted.', 'ok');
    openBackups(id);
  } catch (e) {
    toast('Delete failed: ' + e.message, 'err');
  }
}

function profileForDiscovery(x) {
  return PROFILES.find(v => v.key === x.presetKey)
    || PROFILES.find(v => v.appId && v.appId === x.steamAppId)
    || null;
}

function discoveredDef(x) {
  const g = profileForDiscovery(x);
  const path = x.installPath.replace(/[\\/]+$/, '');
  const base = slugify(x.displayName) || ('server-' + x.steamAppId);
  let id = base;
  let n = 2;
  while (SERVERS.some(s => s.id === id)) id = base + '-' + n++;

  return {
    id,
    name: x.displayName,
    vmName: '',
    gameType: g?.gameType || 'SteamGeneric',
    guestExePath: x.exePath,
    guestWorkingDir: path,
    startArgs: expandList(g?.args, path),
    saveDirs: expandList(g?.saveRel, path),
    steamAppId: x.steamAppId,
    guestCredentialId: null,
    logPathInGuest: g?.logRel ? expandRoot(g.logRel, path) : null,
    rconHost: g?.caps?.rcon ? 'auto' : null,
    rconPort: g?.caps?.rcon ? (g.rconPort || null) : null,
    rconPassword: null,
    hostingMode: 'BareMetal',
    scheduledTaskName: null,
    stopProcessNames: null,
    discordWebhookUrl: null
  };
}

async function openDiscover() {
  openModal('Discover installed servers', '<div class="muted">Scanning Steam libraries and known game-server locations…</div>', '', true);
  try {
    const r = await api('/api/discover');
    const list = r.servers || [];
    window.__discover = list;
    const libs = (r.librariesScanned || []).map(x => `<span class="pill">${esc(x)}</span>`).join('');
    const cards = list.map((x, i) => {
      const g = profileForDiscovery(x);
      return `<div class="job"><div class="jobtop"><div style="font-size:24px">${g?.icon || '🖥'}</div><div class="grow"><div class="jobname">${esc(x.displayName)}</div><div class="jobphase">${esc(x.installPath)} · App ${esc(x.steamAppId)} · ${esc(x.source)}</div></div><span class="state ${x.alreadyConfigured ? 'up' : 'warn'}">${x.alreadyConfigured ? 'ADDED' : 'FOUND'}</span></div>${g?.note ? `<div class="help">${esc(g.note)}</div>` : ''}<div class="jobbuttons">${x.alreadyConfigured ? '<button class="btn" disabled>Already configured</button>' : `<button class="btn primary" ${uiAction('addDiscovered',[i])}>Add with game defaults</button>`}</div></div>`;
    }).join('');

    $('#modalBody').innerHTML = `<div class="summarybar"><div class="muted tiny">${list.length} detected install${list.length === 1 ? '' : 's'}</div></div>${libs ? `<div class="pills" style="margin-bottom:14px">${libs}</div>` : ''}${cards || '<div class="empty">No supported dedicated-server installs were detected.</div>'}`;
  } catch (e) {
    $('#modalBody').innerHTML = `<div class="empty">${esc(e.message)}</div>`;
  }
}

async function addDiscovered(i) {
  const x = (window.__discover || [])[i];
  if (!x) return;
  const def = discoveredDef(x);
  try {
    await api('/api/servers', { method: 'POST', body: JSON.stringify(def) });
    toast('Added ' + def.name + ' with game defaults.', 'ok');
    await loadServers();
    await openDiscover();
  } catch (e) {
    toast('Add failed: ' + e.message, 'err');
  }
}

function existingProfiles() {
  return PROFILES.filter(g => g.key !== 'custom');
}

function openAddExisting() {
  const options = existingProfiles().map(g => `<option value="${esc(g.key)}">${esc(g.label)}</option>`).join('');
  openModal(
    'Add existing server',
    `<div class="callout info">Registers an existing installation. Pick its game profile first so saves, logs and supported features can be filled automatically.</div><div class="field"><label>Game profile</label><select id="aProfile" ${uiAction('applyExistingProfile',[],{event:'change'})}><option value="">Custom / unknown</option>${options}</select></div><div class="formgrid"><div class="field"><label>Name</label><input id="aName" placeholder="My Server"></div><div class="field"><label>Server ID</label><input id="aId" placeholder="my-server"></div></div><div class="field"><label>Server folder</label><input id="aWork" class="mono" placeholder="C:\\GameServers\\Example" ${uiAction('applyExistingProfile',[false],{event:'input'})}></div><div class="field"><label>Server executable</label><input id="aExe" class="mono"></div><div class="formgrid"><div class="field"><label>Save folders</label><textarea id="aSaves" class="mono"></textarea></div><div id="aLogWrap" class="field"><label>Log file</label><input id="aLog" class="mono"></div></div><details class="advanced"><summary>Advanced settings</summary><div class="advanced-body"><div class="field"><label>Start args — one per line</label><textarea id="aArgs" class="mono"></textarea></div><div class="formgrid three"><div class="field"><label>Steam App ID</label><input id="aSteam"></div><div class="field"><label>Game type</label><select id="aGame"><option>SteamGeneric</option><option>Windrose</option><option>Minecraft</option><option>Custom</option></select></div></div><div id="aRcon" class="hidden"><div class="section">RCON</div><div class="formgrid three"><div class="field"><label>Host</label><input id="aRhost" value="auto"></div><div class="field"><label>Port</label><input id="aRport" type="number" min="1" max="65535"></div><div class="field"><label>Password</label><input id="aRpass" type="password"></div></div></div></div></details>`,
    `<button class="btn" ${uiAction('closeModal')}>Cancel</button><button class="btn primary" ${uiAction('saveAddExisting')}>Add server</button>`,
    true
  );
}

function applyExistingProfile(resetName = true) {
  const g = existingProfiles().find(x => x.key === $('#aProfile')?.value);
  const root = $('#aWork')?.value.trim().replace(/[\\/]+$/, '') || '';
  if (!g) {
    $('#aRcon')?.classList.add('hidden');
    return;
  }

  if (resetName) {
    $('#aName').value = g.label;
    $('#aId').value = slugify(g.label);
    if (!root) $('#aWork').value = `C:\\GameServers\\${g.dir}`;
  }

  const p = $('#aWork').value.trim().replace(/[\\/]+$/, '');
  $('#aExe').value = joinPath(p, g.exe);
  $('#aArgs').value = expandList(g.args, p).join('\n');
  $('#aSaves').value = expandList(g.saveRel, p).join('\n');
  $('#aSteam').value = g.appId || '';
  $('#aGame').value = g.gameType || 'SteamGeneric';
  $('#aLog').value = g.logRel ? expandRoot(g.logRel, p) : '';
  $('#aLogWrap').classList.toggle('hidden', !g.caps?.log);
  $('#aRcon').classList.toggle('hidden', !g.caps?.rcon);
  if (g.caps?.rcon && $('#aRport') && !$('#aRport').value) $('#aRport').value = g.rconPort || '';
}

async function saveAddExisting() {
  const id = $('#aId').value.trim();
  const name = $('#aName').value.trim();
  const exe = $('#aExe').value.trim();
  const work = $('#aWork').value.trim();
  const g = existingProfiles().find(x => x.key === $('#aProfile').value);
  const rcon = !!g?.caps?.rcon;

  if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(id) || !name || !exe || !work) {
    return toast('Valid ID, name, executable and server folder are required.', 'err');
  }

  const def = {
    id,
    name,
    vmName: '',
    gameType: $('#aGame').value,
    guestExePath: exe,
    guestWorkingDir: work,
    startArgs: splitLines($('#aArgs').value),
    saveDirs: splitLines($('#aSaves').value),
    steamAppId: $('#aSteam').value.trim() || null,
    guestCredentialId: null,
    logPathInGuest: g?.caps?.log ? ($('#aLog').value.trim() || null) : null,
    rconHost: rcon ? ($('#aRhost').value.trim() || 'auto') : null,
    rconPort: rcon ? intOrNull($('#aRport').value) : null,
    rconPassword: rcon ? ($('#aRpass').value || null) : null,
    hostingMode: 'BareMetal',
    scheduledTaskName: null,
    stopProcessNames: null,
    discordWebhookUrl: null
  };

  try {
    await api('/api/servers', { method: 'POST', body: JSON.stringify(def) });
    toast('Server added.', 'ok');
    closeModal();
    await loadServers();
  } catch (e) {
    toast('Add failed: ' + e.message, 'err');
  }
}

let STAT_POINTS = [];

function statPath(values, max, w = 700, h = 150) {
  if (values.length < 2) return '';
  const m = Math.max(max, ...values.map(v => Number(v) || 0), 1);
  return values.map((v, i) => `${i ? 'L' : 'M'} ${(i / Math.max(1, values.length - 1) * w).toFixed(1)} ${(h - (Number(v) || 0) / m * h).toFixed(1)}`).join(' ');
}

function drawStats() {
  const el = $('#statsChart');
  if (!el) return;
  const cpu = STAT_POINTS.map(x => x.cpu || 0);
  const ram = STAT_POINTS.map(x => x.ram || 0);
  const last = STAT_POINTS.at(-1) || {};
  const ramMax = Math.max(1024, ...ram, 1);
  el.innerHTML = `<div class="metrics"><span>CPU <b>${last.cpu != null ? last.cpu.toFixed(1) + '%' : '—'}</b></span><span>RAM <b>${last.ram != null ? last.ram + ' MB' : '—'}</b></span><span>PID <b>${last.pid ?? '—'}</b></span></div><div class="chartTitle">CPU % · last ${STAT_POINTS.length} samples</div><svg class="chart" viewBox="0 0 700 150" preserveAspectRatio="none"><path class="chartGrid" d="M0 75H700 M0 149H700"/><path class="chartCpu" d="${statPath(cpu, 100)}"/></svg><div class="chartTitle">RAM MB · scale ${ramMax} MB</div><svg class="chart" viewBox="0 0 700 150" preserveAspectRatio="none"><path class="chartGrid" d="M0 75H700 M0 149H700"/><path class="chartRam" d="${statPath(ram, ramMax)}"/></svg>`;
}

async function sampleStats(id) {
  try {
    const s = await api(`/api/servers/${encodeURIComponent(id)}/status`);
    STAT_POINTS.push({ at: Date.now(), cpu: s.cpuPercent ?? 0, ram: s.memoryMB ?? 0, pid: s.pidInGuest });
    if (STAT_POINTS.length > 90) STAT_POINTS.shift();
    drawStats();
  } catch (e) {
    const el = $('#statsErr');
    if (el) el.textContent = e.message;
  }
}

async function openStats(id) {
  const d = SERVERS.find(x => x.id === id);
  STAT_POINTS = [];
  openModal('Stats — ' + (d?.name || id), '<div id="statsErr" class="tiny muted"></div><div id="statsChart"><div class="muted">Collecting samples…</div></div>', `<button class="btn" ${uiAction('closeModal')}>Close</button>`, true);
  await sampleStats(id);
  const timer = setInterval(() => sampleStats(id), 2000);
  MODAL_CLEANUP = () => clearInterval(timer);
}

function appendLiveLog(line) {
  const pane = $('#liveLog');
  if (!pane) return;
  const at = line.at ?? line.At;
  const text = line.text ?? line.Text ?? '';
  const prefix = at ? `[${new Date(at).toLocaleTimeString()}] ` : '';
  pane.textContent += (pane.textContent && pane.textContent !== 'Connecting…' ? '\n' : '') + prefix + text;
  if (pane.textContent.length > 120000) pane.textContent = pane.textContent.slice(-100000);
  pane.scrollTop = pane.scrollHeight;
}

async function openLog(id) {
  const d = SERVERS.find(x => x.id === id);
  if (d?.hostingMode === 'Vm') {
    return openModal('Live log — ' + (d?.name || id), '<div class="empty">Live log tailing is currently available for servers running on this machine. Hyper-V guest log tailing is not implemented yet.</div>', `<button class="btn" ${uiAction('closeModal')}>Close</button>`);
  }

  openModal(
    'Live log — ' + (d?.name || id),
    `<div class="tiny muted" style="margin-bottom:8px">Recent output followed by live server log · ${esc(d?.logPathInGuest || '')}</div><pre id="liveLog" class="log">Connecting…</pre>`,
    `<button class="btn" ${uiAction('closeModal')}>Close</button>`,
    true
  );

  let ws = null;
  let ping = null;
  try {
    const nr = await fetch(CFG.url + '/hubs/status/negotiate?negotiateVersion=1', {
      method: 'POST',
      headers: { Authorization: 'Bearer ' + CFG.token }
    });
    if (!nr.ok) throw new Error('SignalR negotiate HTTP ' + nr.status);

    const n = await nr.json();
    const u = new URL(CFG.url);
    u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:';
    u.pathname = u.pathname.replace(/\/$/, '') + '/hubs/status';
    u.search = '';
    u.searchParams.set('id', n.connectionToken);
    u.searchParams.set('access_token', CFG.token);
    ws = new WebSocket(u.toString());

    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('WebSocket connection timed out.')), 7000);
      ws.onopen = () => {
        clearTimeout(timer);
        ws.send('{"protocol":"json","version":1}\u001e');
        resolve();
      };
      ws.onerror = () => {
        clearTimeout(timer);
        reject(new Error('WebSocket connection failed.'));
      };
    });

    ws.onmessage = ev => {
      for (const raw of String(ev.data).split('\u001e')) {
        if (!raw) continue;
        try {
          const m = JSON.parse(raw);
          if (m.type === 1 && String(m.target).toLowerCase() === 'logline') {
            const l = m.arguments?.[0];
            const sid = l?.serverId ?? l?.ServerId;
            if (l && sid === id) appendLiveLog(l);
          }
        } catch { }
      }
    };

    ping = setInterval(() => {
      if (ws?.readyState === WebSocket.OPEN) ws.send('{"type":6}\u001e');
    }, 15000);

    await api(`/api/servers/${encodeURIComponent(id)}/logs/tail`, {
      method: 'POST',
      body: JSON.stringify({ action: 'start' })
    });
    $('#liveLog').textContent = '';

    MODAL_CLEANUP = () => {
      if (ping) clearInterval(ping);
      try { ws?.close(); } catch { }
      api(`/api/servers/${encodeURIComponent(id)}/logs/tail`, {
        method: 'POST',
        body: JSON.stringify({ action: 'stop' })
      }).catch(() => {});
    };
  } catch (e) {
    if (ping) clearInterval(ping);
    try { ws?.close(); } catch { }
    $('#liveLog').textContent = 'Could not open live log: ' + e.message;
  }
}

async function openTokens() {
  PENDING_TOKEN_REPLACEMENT = null;
  openModal('API tokens', '<div id="tokensPane" class="muted">Loading…</div>', `<button class="btn" ${uiAction('closeModal')}>Close</button>`, true);
  await loadTokens();
}

async function loadTokens() {
  const pane = $('#tokensPane');
  if (!pane) return;
  try {
    const list = await api('/api/tokens');
    pane.innerHTML = `<div class="callout info">Tokens authorize Agent API access. Raw token values are shown only once when created.</div><div class="section">Existing tokens</div>${list.length ? list.map(t => `<div class="job"><div class="jobtop"><div class="grow"><div class="jobname">${esc(t.name)}</div><div class="jobphase">${esc(t.id)} · ${esc(t.role)} · ${esc(t.token || '')}</div></div><button class="btn" ${uiAction('replaceToken',[t.id,t.name,t.role])}>Replace</button><button class="btn danger" ${uiAction('deleteToken',[t.id])}>Delete</button></div></div>`).join('') : '<div class="muted">No secondary tokens.</div>'}<div class="section">Create token</div><div class="formgrid three"><div class="field"><label>ID</label><input id="tokId" placeholder="laptop"></div><div class="field"><label>Name</label><input id="tokName" placeholder="Laptop admin"></div><div class="field"><label>Role</label><select id="tokRole"><option>Admin</option><option>ReadOnly</option></select></div></div><button class="btn primary" ${uiAction('createToken')}>Create token</button><div id="newTokenBox"></div>`;
  } catch (e) {
    pane.innerHTML = `<div class="empty">${esc(e.message)}<br><br>This action requires an Admin token.</div>`;
  }
}

async function createToken() {
  const id = $('#tokId').value.trim();
  const name = $('#tokName').value.trim();
  const role = $('#tokRole').value;
  if (!id || !name) return toast('Token ID and name are required.', 'err');

  try {
    const r = await api('/api/tokens', { method: 'POST', body: JSON.stringify({ id, name, role }) });
    const raw = r.token || '';
    await loadTokens();
    const box = $('#newTokenBox');
    if (box) {
      box.innerHTML = `<div class="callout info"><b>Copy this token now — it will not be shown again.</b><div class="consolebar"><input id="rawNewToken" class="input mono" readonly value="${esc(raw)}"><button class="btn" ${uiAction('copyNewToken')}>Copy</button></div></div>`;
    }
    toast('Token created.', 'ok');
  } catch (e) {
    toast('Token create failed: ' + e.message, 'err');
  }
}

async function copyNewToken() {
  const value = $('#rawNewToken')?.value || '';
  if (!value) return;
  try { await navigator.clipboard.writeText(value); toast('Token copied.', 'ok'); }
  catch (e) { toast('Could not copy token: ' + e.message, 'err'); }
}

// Rotate a managed token in two steps so the new credential is copied before revocation.
let PENDING_TOKEN_REPLACEMENT = null;
async function replaceToken(oldId, name, role) {
  if (PENDING_TOKEN_REPLACEMENT) return toast('Finish or cancel the pending token replacement first.', 'err');
  if (!confirm('Create a new ' + role + ' token to replace ' + oldId + '? The old token stays valid until you confirm revocation.')) return;
  const id = 'replacement-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 6);
  try {
    const r = await api('/api/tokens', {
      method: 'POST', body: JSON.stringify({ id, name: name + ' (replacement)', role })
    });
    PENDING_TOKEN_REPLACEMENT = { oldId, id };
    await loadTokens();
    const box = $('#newTokenBox');
    if (box) {
      box.innerHTML = `<div class="callout info"><b>New token created. Copy and save it before revoking the old token.</b><div class="consolebar"><input id="rawNewToken" class="input mono" readonly value="${esc(r.token || '')}"><button class="btn" ${uiAction('copyNewToken')}>Copy</button></div><button class="btn danger" ${uiAction('finishTokenReplacement')}>I've saved it — revoke old token</button><button class="btn" ${uiAction('cancelTokenReplacement')}>Keep both</button></div>`;
    }
  } catch (e) { toast('Token replacement failed: ' + e.message, 'err'); }
}
async function finishTokenReplacement() {
  const pending = PENDING_TOKEN_REPLACEMENT;
  if (!pending || !confirm('Revoke the old token ' + pending.oldId + '? Existing sessions using it will lose access.')) return;
  try {
    await api('/api/tokens/' + encodeURIComponent(pending.oldId), { method: 'DELETE' });
    PENDING_TOKEN_REPLACEMENT = null;
    await loadTokens();
    toast('Old token revoked. The replacement is now active.', 'ok');
  } catch (e) { toast('Old token revocation failed: ' + e.message, 'err'); }
}
function cancelTokenReplacement() {
  PENDING_TOKEN_REPLACEMENT = null;
  loadTokens();
}

async function deleteToken(id) {
  if (!confirm('Delete API token ' + id + '?')) return;
  try {
    await api('/api/tokens/' + encodeURIComponent(id), { method: 'DELETE' });
    toast('Token deleted.', 'ok');
    await loadTokens();
  } catch (e) {
    toast('Delete failed: ' + e.message, 'err');
  }
}

/* Safe raw config-file workspace. Loaded before server-workspace.js, so install the
   extension on the next task after that module has created its global functions. */
window.GSC_FILE_EDITOR = { serverId: null, path: null, sha: null };

function gscFormatBytes(n) {
  const value = Number(n || 0);
  if (value < 1024) return value + ' B';
  if (value < 1024 * 1024) return (value / 1024).toFixed(1) + ' KB';
  return (value / 1024 / 1024).toFixed(1) + ' MB';
}

async function renderServerFiles(def, body) {
  const result = await api(`/api/servers/${encodeURIComponent(def.id)}/files`);
  if (!result.supported) {
    body.innerHTML = `<div class="sw-empty">${esc(result.reason || 'Config-file editing is not available for this server.')}</div>`;
    return;
  }

  const files = result.files || [];
  if (!files.length) {
    body.innerHTML = `<div class="sw-toolbar"><div><b>Config files</b><div class="tiny muted">Only safe text configuration roots are scanned; this is not a general file manager.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['files'])}>Rescan</button></div><div class="sw-empty">No supported config files found yet.</div>`;
    return;
  }

  const groups = [...new Set(files.map(x => x.group || 'Config'))];
  body.innerHTML = `<div class="sw-toolbar"><div><b>Config files</b><div class="tiny muted">Server and mod/plugin configuration stored on the game host. Saving creates an automatic history entry.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['files'])}>Rescan</button></div><div class="formgrid"><div class="field"><label>Filter</label><input id="gscFileFilter" placeholder="name, path or group…" ${uiAction('serverConfigFilter',[],{event:'input',pass:'value'})}></div><div class="field"><label>Discovered</label><div class="help" style="padding-top:8px">${files.length} file${files.length === 1 ? '' : 's'} · ${groups.map(esc).join(' · ')}</div></div></div><div id="gscFileList" style="margin-top:10px">${files.map(file => `<div class="sw-row gsc-config-file" data-file-search="${esc(((file.group || '') + ' ' + file.path).toLowerCase())}"><div class="grow"><div class="sw-row-title">${esc(file.name)}</div><div class="sw-row-meta"><b>${esc(file.group || 'Config')}</b> · <span class="mono">${esc(file.path)}</span> · ${gscFormatBytes(file.sizeBytes)} · ${new Date(file.modifiedAt).toLocaleString()}${file.restartRecommended ? ' · restart may be required' : ''}</div></div><button class="btn" ${uiAction('serverConfigOpen',[def.id,file.path])}>Edit</button></div>`).join('')}</div>`;
}

function serverConfigFilter(value) {
  const q = String(value || '').trim().toLowerCase();
  document.querySelectorAll('.gsc-config-file').forEach(row => {
    row.classList.toggle('hidden', !!q && !String(row.dataset.fileSearch || '').includes(q));
  });
}
window.serverConfigFilter = serverConfigFilter;

async function serverConfigOpen(serverId, path) {
  try {
    const file = await api(`/api/servers/${encodeURIComponent(serverId)}/files/content?path=${encodeURIComponent(path)}`);
    GSC_FILE_EDITOR = { serverId, path: file.path, sha: file.sha256 };
    const d = SERVERS.find(x => x.id === serverId);
    openModal(
      'Config file — ' + (d?.name || serverId),
      `<div class="callout info"><b>${esc(file.path)}</b><div class="tiny" style="margin-top:4px">${esc(file.format || 'text').toUpperCase()} · ${gscFormatBytes(file.sizeBytes)} · modified ${new Date(file.modifiedAt).toLocaleString()}</div></div><div class="field"><label>Contents</label><textarea id="gscFileEditor" class="mono" spellcheck="false" style="min-height:52vh;resize:vertical">${esc(file.content)}</textarea><div class="help">JSON and XML are validated before save. Other supported formats are saved as raw text. Changes create an automatic history copy first.</div></div><div class="section">History</div><div id="gscFileHistory" class="muted tiny">Loading history…</div>`,
      `<button class="btn" ${uiAction('closeModal')}>Close</button><button class="btn primary" ${uiAction('serverConfigSave')}>Save</button>`,
      true
    );
    await serverConfigLoadHistory();
  } catch (e) {
    toast('Could not open config file: ' + e.message, 'err');
  }
}
window.serverConfigOpen = serverConfigOpen;

async function serverConfigSave() {
  const state = GSC_FILE_EDITOR;
  const editor = $('#gscFileEditor');
  if (!state.serverId || !state.path || !editor) return;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/content`, {
      method: 'PUT',
      body: JSON.stringify({ path: state.path, content: editor.value, expectedSha256: state.sha })
    });
    state.sha = result.sha256;
    toast('Config file saved. Previous content is in history.', 'ok');
    await serverConfigLoadHistory();
  } catch (e) {
    toast('Config save failed: ' + e.message, 'err');
  }
}
window.serverConfigSave = serverConfigSave;

async function serverConfigLoadHistory() {
  const state = GSC_FILE_EDITOR;
  const pane = $('#gscFileHistory');
  if (!pane || !state.serverId || !state.path) return;
  try {
    const list = await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/history?path=${encodeURIComponent(state.path)}`);
    pane.innerHTML = list?.length
      ? list.map(item => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${new Date(item.createdAt).toLocaleString()}</div><div class="sw-row-meta">${gscFormatBytes(item.sizeBytes)} · ${esc(item.id)}</div></div><button class="btn" ${uiAction('serverConfigRestore',[item.id])}>Restore</button></div>`).join('')
      : '<div class="muted tiny">No previous versions yet.</div>';
  } catch (e) {
    pane.innerHTML = `<div class="callout">${esc(e.message)}</div>`;
  }
}
window.serverConfigLoadHistory = serverConfigLoadHistory;

async function serverConfigRestore(backupId) {
  const state = GSC_FILE_EDITOR;
  if (!state.serverId || !state.path) return;
  if (!confirm('Restore this previous version? The current content will be saved as another history entry first.')) return;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/restore`, {
      method: 'POST',
      body: JSON.stringify({ path: state.path, backupId, expectedSha256: state.sha })
    });
    state.sha = result.sha256;
    const fresh = await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/content?path=${encodeURIComponent(state.path)}`);
    state.sha = fresh.sha256;
    const editor = $('#gscFileEditor');
    if (editor) editor.value = fresh.content;
    toast('Config history version restored.', 'ok');
    await serverConfigLoadHistory();
  } catch (e) {
    toast('Restore failed: ' + e.message, 'err');
  }
}
window.serverConfigRestore = serverConfigRestore;

(function installServerFileWorkspace() {
  let attempts = 0;
  const install = () => {
    if (typeof serverWorkspaceTabs !== 'function' || typeof renderServerWorkspaceTab !== 'function') {
      if (++attempts < 200) setTimeout(install, 10);
      return;
    }

    if (window.__gscFileWorkspaceInstalled) return;
    window.__gscFileWorkspaceInstalled = true;

    const baseTabs = serverWorkspaceTabs;
    serverWorkspaceTabs = function (def) {
      const tabs = baseTabs(def);
      if (!tabs.some(x => x[0] === 'files')) {
        const configIndex = tabs.findIndex(x => x[0] === 'config');
        const modsIndex = tabs.findIndex(x => x[0] === 'mods');
        const insertAt = configIndex >= 0 ? configIndex + 1 : modsIndex >= 0 ? modsIndex + 1 : Math.min(2, tabs.length);
        tabs.splice(insertAt, 0, ['files', 'Files']);
      }
      return tabs;
    };

    const baseRenderTab = renderServerWorkspaceTab;
    renderServerWorkspaceTab = async function (def, tab) {
      if (tab !== 'files') return baseRenderTab(def, tab);
      const token = ++SERVER_WORKSPACE.renderToken;
      const body = $('#swTabBody');
      if (!body) return;
      body.innerHTML = '<div class="muted">Scanning config files…</div>';
      try {
        await renderServerFiles(def, body);
        if (token !== SERVER_WORKSPACE.renderToken) return;
      } catch (e) {
        body.innerHTML = `<div class="callout">${esc(e.message)}</div>`;
      }
    };

    if (typeof renderServerMods === 'function') {
      const baseRenderMods = renderServerMods;
      renderServerMods = function (def, body) {
        baseRenderMods(def, body);
        const section = document.createElement('div');
        section.className = 'sw-section';
        section.innerHTML = `<h3>Mod / plugin configuration</h3><div class="muted tiny" style="margin-bottom:12px">Config files discovered in supported mod/plugin folders can be edited without uploading the mod archive through the panel.</div><button class="btn" ${uiAction('serverWorkspaceSetTab',['files'])}>Open config files</button>`;
        body.appendChild(section);
      };
    }
  };
  setTimeout(install, 0);
})();

/* Capability-aware server administration. Raw RCON remains available only in the dedicated
   console; this workspace exposes the standard commands explicitly advertised by the handler. */
async function renderServerAdmin(def, body) {
  body.innerHTML = '<div class="muted">Loading admin capabilities…</div>';
  try {
    const cap = await api(`/api/servers/${encodeURIComponent(def.id)}/admin/capabilities`);
    const commands = new Set((cap.commands || []).map(String));
    if (!cap.supported) {
      body.innerHTML = '<div class="sw-empty">This game does not expose standard RCON administration actions.</div>';
      return;
    }
    if (!cap.configured) {
      body.innerHTML = '<div class="callout info">RCON is supported, but this server needs an RCON port and password before admin actions can run.</div>';
      return;
    }
    const save = commands.has('Save') ? `<button class="btn" ${uiAction('serverWorkspaceAdminCommand',[def.id,'Save'])}>Save world</button>` : '';
    const broadcast = commands.has('BroadcastMessage') ? `<div class="sw-section"><h3>Broadcast message</h3><div class="sw-toolbar"><input id="swAdminBroadcast" class="grow" maxlength="500" placeholder="Message to connected players"><button class="btn primary" ${uiAction('serverWorkspaceAdminCommand',[def.id,'BroadcastMessage'],{pass:'selectorValue',selector:'#swAdminBroadcast'})}>Broadcast</button></div></div>` : '';
    const shutdown = commands.has('Shutdown') ? `<button class="btn danger" ${uiAction('serverWorkspaceAdminCommand',[def.id,'Shutdown'])}>RCON shutdown</button>` : '';
    const playerHint = commands.has('KickPlayer') || commands.has('BanPlayer') ? '<div class="callout info">Kick and ban actions remain in the Players tab so the player identifier is selected safely.</div>' : '';
    body.innerHTML = `<div class="sw-section"><div class="sw-toolbar"><div><h3 style="margin:0">Server administration</h3><div class="tiny muted">Only standard commands advertised by the game-specific RCON handler are shown.</div></div><div class="spacer"></div>${save}${shutdown}</div></div>${broadcast}${playerHint}`;
  } catch (e) {
    body.innerHTML = `<div class="callout">Admin capabilities failed: ${esc(e.message)}</div>`;
  }
}

window.serverWorkspaceAdminCommand = async function (id, command, payload = null) {
  if (command === 'Shutdown' && !confirm('Send the game server its RCON shutdown command?')) return;
  if (command === 'BroadcastMessage' && !String(payload || '').trim()) return toast('Enter a broadcast message.', 'err');
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/rcon/command`, {
      method: 'POST',
      body: JSON.stringify({ command, payload: payload == null ? null : String(payload) })
    });
    toast(result?.success ? `${command} completed.` : `${command} failed: ${result?.error || 'unknown error'}`, result?.success ? 'ok' : 'err');
  } catch (e) {
    toast(`${command} failed: ${e.message}`, 'err');
  }
};

(function installServerAdminWorkspace() {
  let attempts = 0;
  const install = () => {
    if (typeof serverWorkspaceTabs !== 'function' || typeof renderServerWorkspaceTab !== 'function') {
      if (++attempts < 200) setTimeout(install, 10);
      return;
    }
    if (window.__gscAdminWorkspaceInstalled) return;
    window.__gscAdminWorkspaceInstalled = true;

    const baseTabs = serverWorkspaceTabs;
    serverWorkspaceTabs = function (def) {
      const tabs = baseTabs(def);
      const profile = profileFor(def);
      if (profile?.caps?.rcon && !tabs.some(x => x[0] === 'admin')) {
        const playersIndex = tabs.findIndex(x => x[0] === 'players');
        tabs.splice(playersIndex >= 0 ? playersIndex + 1 : Math.min(2, tabs.length), 0, ['admin', 'Admin']);
      }
      return tabs;
    };

    const baseRender = renderServerWorkspaceTab;
    renderServerWorkspaceTab = async function (def, tab) {
      if (tab !== 'admin') return baseRender(def, tab);
      const body = $('#swTabBody');
      if (!body) return;
      await renderServerAdmin(def, body);
    };
  };
  setTimeout(install, 0);
})();