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
      `<button class="btn" onclick="closeModal()">Cancel</button><button class="btn primary" onclick="saveConfig('${jsq(id)}')">Save</button>`,
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
      ? (list || []).map(b => `<div class="job"><div class="jobname">${esc(b.checkpointName)}</div><div class="jobphase">${new Date(b.createdAt).toLocaleString()} · ${Math.round((b.sizeBytes || 0) / 1024 / 1024)} MB</div><div class="jobbuttons"><button class="btn" onclick="restoreBackup('${jsq(id)}','${encodeURIComponent(b.checkpointName)}')">Restore</button><button class="btn danger" onclick="deleteBackup('${jsq(id)}','${encodeURIComponent(b.checkpointName)}')">Delete</button></div></div>`).join('')
      : '<div class="empty">No backups yet.</div>';

    openModal(
      'Backups — ' + (d?.name || id),
      body,
      `<button class="btn primary" onclick="action('${jsq(id)}','backup');closeModal()">Backup now</button><button class="btn" onclick="closeModal()">Close</button>`,
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
      return `<div class="job"><div class="jobtop"><div style="font-size:24px">${g?.icon || '🖥'}</div><div class="grow"><div class="jobname">${esc(x.displayName)}</div><div class="jobphase">${esc(x.installPath)} · App ${esc(x.steamAppId)} · ${esc(x.source)}</div></div><span class="state ${x.alreadyConfigured ? 'up' : 'warn'}">${x.alreadyConfigured ? 'ADDED' : 'FOUND'}</span></div>${g?.note ? `<div class="help">${esc(g.note)}</div>` : ''}<div class="jobbuttons">${x.alreadyConfigured ? '<button class="btn" disabled>Already configured</button>' : `<button class="btn primary" onclick="addDiscovered(${i})">Add with game defaults</button>`}</div></div>`;
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
    `<div class="callout info">Registers an existing installation. Pick its game profile first so saves, logs and supported features can be filled automatically.</div><div class="field"><label>Game profile</label><select id="aProfile" onchange="applyExistingProfile()"><option value="">Custom / unknown</option>${options}</select></div><div class="formgrid"><div class="field"><label>Name</label><input id="aName" placeholder="My Server"></div><div class="field"><label>Server ID</label><input id="aId" placeholder="my-server"></div></div><div class="field"><label>Server folder</label><input id="aWork" class="mono" placeholder="C:\\GameServers\\Example" oninput="applyExistingProfile(false)"></div><div class="field"><label>Server executable</label><input id="aExe" class="mono"></div><div class="formgrid"><div class="field"><label>Save folders</label><textarea id="aSaves" class="mono"></textarea></div><div id="aLogWrap" class="field"><label>Log file</label><input id="aLog" class="mono"></div></div><details class="advanced"><summary>Advanced settings</summary><div class="advanced-body"><div class="field"><label>Start args — one per line</label><textarea id="aArgs" class="mono"></textarea></div><div class="formgrid"><div class="field"><label>Steam App ID</label><input id="aSteam"></div><div class="field"><label>Game type</label><select id="aGame"><option>SteamGeneric</option><option>Windrose</option><option>Minecraft</option><option>Custom</option></select></div></div><div id="aRcon" class="hidden"><div class="section">RCON</div><div class="formgrid three"><div class="field"><label>Host</label><input id="aRhost" value="auto"></div><div class="field"><label>Port</label><input id="aRport" type="number" min="1" max="65535"></div><div class="field"><label>Password</label><input id="aRpass" type="password"></div></div></div></div></details>`,
    '<button class="btn" onclick="closeModal()">Cancel</button><button class="btn primary" onclick="saveAddExisting()">Add server</button>',
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
  openModal('Stats — ' + (d?.name || id), '<div id="statsErr" class="tiny muted"></div><div id="statsChart"><div class="muted">Collecting samples…</div></div>', '<button class="btn" onclick="closeModal()">Close</button>', true);
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
    return openModal('Live log — ' + (d?.name || id), '<div class="empty">Live log tailing is currently available for servers running on this machine. Hyper-V guest log tailing is not implemented yet.</div>', '<button class="btn" onclick="closeModal()">Close</button>');
  }

  openModal(
    'Live log — ' + (d?.name || id),
    `<div class="tiny muted" style="margin-bottom:8px">Recent output followed by live server log · ${esc(d?.logPathInGuest || '')}</div><pre id="liveLog" class="log">Connecting…</pre>`,
    '<button class="btn" onclick="closeModal()">Close</button>',
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
  openModal('API tokens', '<div id="tokensPane" class="muted">Loading…</div>', '<button class="btn" onclick="closeModal()">Close</button>', true);
  await loadTokens();
}

async function loadTokens() {
  const pane = $('#tokensPane');
  if (!pane) return;
  try {
    const list = await api('/api/tokens');
    pane.innerHTML = `<div class="callout info">Tokens authorize Agent API access. Raw token values are shown only once when created.</div><div class="section">Existing tokens</div>${list.length ? list.map(t => `<div class="job"><div class="jobtop"><div class="grow"><div class="jobname">${esc(t.name)}</div><div class="jobphase">${esc(t.id)} · ${esc(t.role)} · ${esc(t.token || '')}</div></div><button class="btn danger" onclick="deleteToken('${jsq(t.id)}')">Delete</button></div></div>`).join('') : '<div class="muted">No secondary tokens.</div>'}<div class="section">Create token</div><div class="formgrid three"><div class="field"><label>ID</label><input id="tokId" placeholder="laptop"></div><div class="field"><label>Name</label><input id="tokName" placeholder="Laptop admin"></div><div class="field"><label>Role</label><select id="tokRole"><option>Admin</option><option>ReadOnly</option></select></div></div><button class="btn primary" onclick="createToken()">Create token</button><div id="newTokenBox"></div>`;
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
      box.innerHTML = `<div class="callout info"><b>Copy this token now — it will not be shown again.</b><div class="consolebar"><input id="rawNewToken" class="input mono" readonly value="${esc(raw)}"><button class="btn" onclick="navigator.clipboard.writeText($('#rawNewToken').value);toast('Token copied.','ok')">Copy</button></div></div>`;
    }
    toast('Token created.', 'ok');
  } catch (e) {
    toast('Token create failed: ' + e.message, 'err');
  }
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
