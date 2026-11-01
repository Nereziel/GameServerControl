/* Console-first server workspace inspired by dedicated server control panels.
   This module only changes Web UI information architecture; the existing admin
   modules remain the source of truth for CRUD/install/mod operations. */

profileToGame = function (p) {
  const c = p.capabilities || {};
  const consoleMode = String(c.consoleMode || '').toLowerCase();
  return {
    key: p.key,
    label: p.label,
    icon: p.icon || '🖥',
    appId: p.steamAppId,
    size: p.estimatedSize || 'varies',
    dir: p.installDirectoryName || p.key,
    exe: p.executableRelative || '',
    args: p.defaultStartArgs || [],
    saveRel: p.saveDirs || [],
    logRel: p.logPath || null,
    gameType: p.gameType || 'SteamGeneric',
    rconPort: p.defaultRconPort ?? null,
    note: p.notes || '',
    installable: !!p.installable,
    ports: p.defaultPorts || [],
    caps: {
      config: !!c.managedConfig,
      mods: !!c.mods,
      rcon: !!c.rcon,
      players: !!c.playerManagement,
      log: !!c.liveLog,
      console: consoleMode === 'rcon' ? 'rcon' : consoleMode === 'logonly' ? 'log' : consoleMode === 'stdin' ? 'stdin' : null,
      hyperV: c.hyperV !== false,
      scheduledTask: c.scheduledTask !== false
    }
  };
};

const SERVER_WORKSPACE = {
  serverId: null,
  tab: 'console',
  console: null,
  renderToken: 0
};
const SERVER_WORKSPACE_BASE_SHOW_VIEW = showView;

function installServerWorkspaceStyles() {
  if (document.getElementById('serverWorkspaceStyles')) return;
  const style = document.createElement('style');
  style.id = 'serverWorkspaceStyles';
  style.textContent = `
    .sw-dashboard-head{display:flex;align-items:end;gap:16px;margin-bottom:16px}.sw-dashboard-head h2{margin:0;font-size:22px}.sw-server-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:12px}.sw-server-card{display:block;width:100%;text-align:left;border:1px solid var(--line);background:var(--panel);color:var(--text);border-radius:11px;padding:16px;cursor:pointer;transition:.14s}.sw-server-card:hover{border-color:#66717f;background:#181d24;transform:translateY(-1px)}.sw-card-head{display:flex;align-items:center;gap:12px}.sw-card-icon{font-size:28px;width:38px;text-align:center}.sw-card-title{font-size:16px;font-weight:850}.sw-card-sub{color:var(--muted);font-size:11px;margin-top:3px}.sw-card-state{margin-left:auto;font-size:10px;font-weight:850;text-transform:uppercase;letter-spacing:.5px}.sw-card-state.up{color:var(--ok)}.sw-card-state.warn{color:var(--warn)}.sw-card-state.down{color:var(--muted)}.sw-card-metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:7px;margin-top:14px}.sw-card-metrics span{border:1px solid var(--line);background:#101318;border-radius:7px;padding:8px;color:var(--muted);font-size:10px;text-transform:uppercase}.sw-card-metrics b{display:block;color:var(--text);font-size:13px;margin-top:2px;text-transform:none}
    .sw-workspace{border:1px solid var(--line);background:var(--panel);border-radius:12px;overflow:hidden}.sw-head{display:flex;gap:14px;align-items:center;padding:16px 18px;border-bottom:1px solid var(--line);background:#101318}.sw-back{border:0;background:transparent;color:var(--muted);font-size:20px;cursor:pointer;padding:5px 8px}.sw-back:hover{color:var(--text)}.sw-icon{font-size:29px}.sw-name{font-size:19px;font-weight:900}.sw-meta{color:var(--muted);font-size:11px;margin-top:2px}.sw-actions{display:flex;gap:7px;margin-left:auto;align-items:center}.sw-power{min-width:84px}.sw-state{font-size:10px;font-weight:850;padding:5px 9px;border:1px solid var(--line);border-radius:999px;text-transform:uppercase}.sw-state.up{color:var(--ok);border-color:rgba(63,185,80,.45)}.sw-state.warn{color:var(--warn);border-color:rgba(210,153,34,.45)}.sw-state.down{color:var(--muted)}
    .sw-tabs{display:flex;gap:2px;padding:0 14px;border-bottom:1px solid var(--line);background:#0d1014;overflow-x:auto}.sw-tab{border:0;border-bottom:2px solid transparent;background:transparent;color:var(--muted);padding:12px 11px;font-size:12px;font-weight:750;white-space:nowrap;cursor:pointer}.sw-tab:hover{color:var(--text)}.sw-tab.active{color:var(--text);border-bottom-color:var(--accent)}.sw-body{padding:17px;min-height:480px}.sw-operation{margin-bottom:12px}.sw-metrics{display:grid;grid-template-columns:repeat(4,minmax(110px,1fr));gap:9px;margin-bottom:15px}.sw-metric{border:1px solid var(--line);background:#101318;border-radius:8px;padding:11px}.sw-metric span{display:block;color:var(--muted);font-size:10px;text-transform:uppercase;letter-spacing:.45px}.sw-metric b{display:block;margin-top:4px;font-size:15px}.sw-info{display:grid;grid-template-columns:180px minmax(0,1fr);gap:8px 16px;border:1px solid var(--line);border-radius:9px;padding:14px}.sw-info>div:nth-child(odd){color:var(--muted);font-size:12px}.sw-info>div:nth-child(even){font-size:12px;overflow-wrap:anywhere}
    .sw-console{border:1px solid #29303a;border-radius:9px;overflow:hidden;background:#07090c}.sw-console-head{display:flex;align-items:center;gap:8px;padding:9px 11px;border-bottom:1px solid #252b33;background:#0d1117;color:var(--muted);font-size:11px}.sw-console-dot{width:7px;height:7px;border-radius:50%;background:var(--ok)}.sw-terminal{margin:0;min-height:430px;max-height:58vh;overflow:auto;padding:13px 14px;background:#07090c;color:#d7dde6;font:12px/1.55 ui-monospace,SFMono-Regular,Consolas,monospace;white-space:pre-wrap;word-break:break-word}.sw-consolebar{display:flex;gap:8px;padding:10px;border-top:1px solid #252b33;background:#0d1117}.sw-consolebar input{flex:1;background:#090c10;border:1px solid #343c48;color:var(--text);border-radius:6px;padding:9px 10px;font-family:ui-monospace,SFMono-Regular,Consolas,monospace}.sw-consolebar input:disabled{opacity:.55}.sw-help{color:var(--muted);font-size:11px;padding:8px 11px;border-top:1px solid #20262e;background:#0a0d11}.sw-toolbar{display:flex;gap:8px;align-items:center;margin-bottom:12px}.sw-toolbar .spacer{flex:1}.sw-row{display:flex;gap:12px;align-items:center;padding:11px 12px;border:1px solid var(--line);border-radius:8px;margin-bottom:7px;background:#101318}.sw-row .grow{flex:1;min-width:0}.sw-row-title{font-weight:750}.sw-row-meta{font-size:11px;color:var(--muted);margin-top:2px}.sw-section{border:1px solid var(--line);border-radius:9px;padding:14px;background:#101318;margin-bottom:12px}.sw-section h3{font-size:14px;margin:0 0 10px}.sw-empty{padding:35px;text-align:center;color:var(--muted);border:1px dashed var(--line);border-radius:9px}
    @media(max-width:850px){.sw-head{align-items:flex-start;flex-wrap:wrap}.sw-actions{width:100%;margin-left:0}.sw-actions .sw-state{margin-left:auto}.sw-metrics{grid-template-columns:repeat(2,1fr)}.sw-info{grid-template-columns:1fr}.sw-info>div:nth-child(odd){margin-top:6px}.sw-terminal{min-height:330px}.sw-server-grid{grid-template-columns:1fr}}
    @media(max-width:520px){.sw-actions{display:grid;grid-template-columns:repeat(3,1fr)}.sw-actions .sw-state{grid-column:1/-1;margin:0;text-align:center}.sw-power{min-width:0}.sw-body{padding:12px}.sw-card-metrics{grid-template-columns:1fr 1fr 1fr}}
  `;
  document.head.appendChild(style);
}

function serverWorkspaceParseHash() {
  const match = location.hash.match(/^#server\/([^/]+)(?:\/([^/]+))?$/);
  if (!match) {
    SERVER_WORKSPACE.serverId = null;
    SERVER_WORKSPACE.tab = 'console';
    return;
  }
  try { SERVER_WORKSPACE.serverId = decodeURIComponent(match[1]); }
  catch { SERVER_WORKSPACE.serverId = match[1]; }
  SERVER_WORKSPACE.tab = match[2] || 'console';
}

function serverWorkspaceCleanupConsole() {
  if (!SERVER_WORKSPACE.console) return;
  try { SERVER_WORKSPACE.console.close(); } catch { }
  SERVER_WORKSPACE.console = null;
}

function openServerPage(id, tab = 'console') {
  SERVER_WORKSPACE_BASE_SHOW_VIEW('servers');
  location.hash = `server/${encodeURIComponent(id)}/${tab}`;
}
function serverWorkspaceHome() {
  serverWorkspaceCleanupConsole();
  location.hash = 'servers';
}
function serverWorkspaceSetTab(tab) {
  if (!SERVER_WORKSPACE.serverId) return;
  location.hash = `server/${encodeURIComponent(SERVER_WORKSPACE.serverId)}/${tab}`;
}
window.openServerPage = openServerPage;
window.serverWorkspaceHome = serverWorkspaceHome;
window.serverWorkspaceSetTab = serverWorkspaceSetTab;

const originalServerWorkspaceShowView = showView;
showView = function (view) {
  if (view === 'servers' && SERVER_WORKSPACE.serverId) {
    serverWorkspaceHome();
    return;
  }
  originalServerWorkspaceShowView(view);
};

function serverWorkspaceStateClass(state) {
  return state === 'Running' ? 'up' : ['Starting', 'Stopping', 'Restarting', 'Working'].includes(state) ? 'warn' : 'down';
}
function serverWorkspaceStatus(id) { return STATUS[id] || {}; }

function serverWorkspaceTabs(def) {
  const caps = profileFor(def)?.caps || {};
  const tabs = [];
  if (caps.console) tabs.push(['console', 'Console']);
  tabs.push(['overview', 'Overview']);
  if (caps.config) tabs.push(['config', 'Configuration']);
  if (caps.players) tabs.push(['players', 'Players']);
  if (caps.mods) tabs.push(['mods', 'Mods']);
  tabs.push(['backups', 'Backups'], ['network', 'Network'], ['schedule', 'Schedule'], ['settings', 'Settings']);
  return tabs;
}

function serverWorkspaceValidTab(def, tab) {
  const tabs = serverWorkspaceTabs(def);
  if (tabs.some(x => x[0] === tab)) return tab;
  return tabs.some(x => x[0] === 'console') ? 'console' : 'overview';
}

renderServers = function () {
  installServerWorkspaceStyles();
  const root = $('#servers');
  if (!root) return;

  if (SERVER_WORKSPACE.serverId) {
    const def = SERVERS.find(x => x.id === SERVER_WORKSPACE.serverId);
    if (def) {
      renderServerWorkspace(def);
      return;
    }
    SERVER_WORKSPACE.serverId = null;
  }

  serverWorkspaceCleanupConsole();
  if (!SERVERS.length) {
    root.innerHTML = '<div class="empty">No servers registered yet.<br><br><button class="btn primary" onclick="openCreate()">Create your first server</button> <button class="btn" onclick="openDiscover()">Discover installed servers</button> <button class="btn" onclick="openAddExisting()">Add existing server</button></div>';
    return;
  }

  root.innerHTML = `<div class="sw-dashboard-head"><div><h2>Servers</h2><div class="muted tiny">Choose a server to open its console and controls.</div></div></div><div class="sw-server-grid">${SERVERS.map(def => {
    const status = serverWorkspaceStatus(def.id);
    const meta = serverMeta(def);
    const state = actionVisual(def.id, status.processState || 'Unknown');
    return `<button class="sw-server-card" onclick="openServerPage('${jsq(def.id)}')"><div class="sw-card-head"><div class="sw-card-icon">${meta.icon}</div><div class="grow"><div class="sw-card-title">${esc(def.name)}</div><div class="sw-card-sub">${esc(meta.label)} · ${def.hostingMode === 'Vm' ? 'Hyper-V' : 'This machine'}</div></div><div class="sw-card-state ${serverWorkspaceStateClass(state)}">${esc(state)}</div></div><div class="sw-card-metrics"><span>CPU<b>${status.cpuPercent != null ? Number(status.cpuPercent).toFixed(1) + '%' : '—'}</b></span><span>RAM<b>${status.memoryMB != null ? status.memoryMB + ' MB' : '—'}</b></span><span>PID<b>${status.pidInGuest ?? '—'}</b></span></div></button>`;
  }).join('')}</div>`;
};

function updateServerWorkspaceHeader(def) {
  const status = serverWorkspaceStatus(def.id);
  const pending = window.GSCUX?.actions?.[def.id];
  const state = pending?.state || status.processState || 'Unknown';
  const running = status.processState === 'Running';
  const busy = !!pending || ['Starting', 'Stopping'].includes(status.processState);

  const stateEl = $('#swState');
  if (stateEl) {
    stateEl.textContent = state;
    stateEl.className = 'sw-state ' + serverWorkspaceStateClass(state);
  }
  const start = $('#swStart'), stop = $('#swStop'), restart = $('#swRestart');
  if (start) start.disabled = running || busy;
  if (stop) stop.disabled = !running || busy;
  if (restart) restart.disabled = !running || busy;

  const operation = $('#swOperation');
  if (operation) {
    operation.innerHTML = pending
      ? `<div class="operation-banner ${pending.error ? 'err' : pending.done ? 'ok' : ''}">${pending.done || pending.error ? '' : '<span class="spinner"></span>'}<div><b>${esc(pending.label || 'Working…')}</b>${pending.detail ? `<div class="tiny muted">${esc(pending.detail)}</div>` : ''}</div></div>`
      : '';
  }

  const cpu = $('#swCpu'), ram = $('#swRam'), pid = $('#swPid');
  if (cpu) cpu.textContent = status.cpuPercent != null ? Number(status.cpuPercent).toFixed(1) + '%' : '—';
  if (ram) ram.textContent = status.memoryMB != null ? status.memoryMB + ' MB' : '—';
  if (pid) pid.textContent = status.pidInGuest ?? '—';
}

function renderServerWorkspace(def) {
  const root = $('#servers');
  const requestedTab = serverWorkspaceValidTab(def, SERVER_WORKSPACE.tab);
  const existing = root.querySelector('.sw-workspace');
  if (existing && existing.dataset.serverId === def.id && existing.dataset.tab === requestedTab) {
    updateServerWorkspaceHeader(def);
    return;
  }

  serverWorkspaceCleanupConsole();
  SERVER_WORKSPACE.tab = requestedTab;
  const meta = serverMeta(def);
  const tabs = serverWorkspaceTabs(def);

  root.innerHTML = `<div class="sw-workspace" data-server-id="${esc(def.id)}" data-tab="${esc(SERVER_WORKSPACE.tab)}"><div class="sw-head"><button class="sw-back" onclick="serverWorkspaceHome()" title="Back to servers">←</button><div class="sw-icon">${meta.icon}</div><div class="grow"><div class="sw-name">${esc(def.name)}</div><div class="sw-meta">${esc(meta.label)} · ${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This machine'}</div></div><div class="sw-actions"><button id="swStart" class="btn primary sw-power" onclick="action('${jsq(def.id)}','start')">Start</button><button id="swRestart" class="btn sw-power" onclick="action('${jsq(def.id)}','restart')">Restart</button><button id="swStop" class="btn danger sw-power" onclick="action('${jsq(def.id)}','stop')">Stop</button><span id="swState" class="sw-state"></span></div></div><div class="sw-tabs">${tabs.map(([key, label]) => `<button class="sw-tab ${SERVER_WORKSPACE.tab === key ? 'active' : ''}" onclick="serverWorkspaceSetTab('${key}')">${label}</button>`).join('')}</div><div class="sw-body"><div id="swOperation" class="sw-operation"></div><div id="swTabBody"><div class="muted">Loading…</div></div></div></div>`;
  updateServerWorkspaceHeader(def);
  renderServerWorkspaceTab(def, SERVER_WORKSPACE.tab);
}

async function renderServerWorkspaceTab(def, tab) {
  const token = ++SERVER_WORKSPACE.renderToken;
  const body = $('#swTabBody');
  if (!body) return;
  body.innerHTML = '<div class="muted">Loading…</div>';
  try {
    if (tab === 'console') await renderServerConsole(def, body);
    else if (tab === 'overview') renderServerOverview(def, body);
    else if (tab === 'config') await renderServerConfig(def, body);
    else if (tab === 'players') await renderServerPlayers(def, body);
    else if (tab === 'mods') renderServerMods(def, body);
    else if (tab === 'backups') await renderServerBackups(def, body);
    else if (tab === 'network') renderServerNetwork(def, body);
    else if (tab === 'schedule') await renderServerSchedule(def, body);
    else if (tab === 'settings') renderServerSettings(def, body);
    if (token !== SERVER_WORKSPACE.renderToken) return;
  } catch (e) {
    body.innerHTML = `<div class="callout">${esc(e.message)}</div>`;
  }
}

function renderServerOverview(def, body) {
  const status = serverWorkspaceStatus(def.id);
  const ports = effectiveNetworkPorts(def);
  const metadata = Object.entries(status.metadata || {}).map(([k, v]) => `<div>${esc(k)}</div><div>${esc(v)}</div>`).join('');
  body.innerHTML = `<div class="sw-metrics"><div class="sw-metric"><span>CPU</span><b id="swCpu">${status.cpuPercent != null ? Number(status.cpuPercent).toFixed(1) + '%' : '—'}</b></div><div class="sw-metric"><span>RAM</span><b id="swRam">${status.memoryMB != null ? status.memoryMB + ' MB' : '—'}</b></div><div class="sw-metric"><span>PID</span><b id="swPid">${status.pidInGuest ?? '—'}</b></div><div class="sw-metric"><span>Last backup</span><b>${status.lastBackupAt ? new Date(status.lastBackupAt).toLocaleString() : '—'}</b></div></div><div class="sw-info"><div>Server folder</div><div class="mono">${esc(def.guestWorkingDir || '')}</div><div>Executable</div><div class="mono">${esc(def.guestExePath || '')}</div><div>Save folders</div><div class="mono">${(def.saveDirs || []).map(esc).join('<br>') || '—'}</div><div>Network</div><div>${ports.length ? ports.map(formatNetworkPort).map(esc).join('<br>') : 'No reserved ports'}</div>${metadata}</div>`;
}

async function renderServerConfig(def, body) {
  const result = await api(`/api/servers/${encodeURIComponent(def.id)}/config`);
  if (!result.schema) {
    body.innerHTML = '<div class="sw-empty">No managed configuration is available for this game.</div>';
    return;
  }
  const values = result.values || {};
  let fields = '';
  for (const section of result.schema.sections || []) {
    fields += `<div class="sw-section"><h3>${esc(section.title)}</h3><div class="formgrid">`;
    for (const field of section.fields || []) fields += configField(field, values[field.key] ?? field.default ?? '');
    fields += '</div></div>';
  }
  body.innerHTML = `<div class="sw-toolbar"><div class="muted tiny">Game-specific settings from the ${esc(profileFor(def)?.label || def.gameType)} profile.</div><div class="spacer"></div><button class="btn primary" onclick="serverWorkspaceSaveConfig('${jsq(def.id)}')">Save configuration</button></div>${fields}`;
}

async function serverWorkspaceSaveConfig(id) {
  const values = {};
  $('#swTabBody')?.querySelectorAll('[data-cfg]').forEach(el => values[el.dataset.cfg] = el.type === 'checkbox' ? String(el.checked) : String(el.value));
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/config`, { method: 'PUT', body: JSON.stringify(values) });
    toast('Configuration saved.', 'ok');
  } catch (e) { toast('Save failed: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveConfig = serverWorkspaceSaveConfig;

async function renderServerPlayers(def, body) {
  const list = await api(`/api/servers/${encodeURIComponent(def.id)}/rcon/players`);
  body.innerHTML = `<div class="sw-toolbar"><div class="muted tiny">Players returned by the game RCON handler.</div><div class="spacer"></div><button class="btn" onclick="serverWorkspaceRefreshTab('players')">Refresh</button></div>${list?.length ? list.map(player => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(player.name)}</div><div class="sw-row-meta">${esc(player.steamId)} · ${esc(player.address || player.playerUid || '')}</div></div><button class="btn" onclick="serverWorkspacePlayerAction('${jsq(def.id)}','KickPlayer','${jsq(player.steamId)}')">Kick</button><button class="btn danger" onclick="serverWorkspacePlayerAction('${jsq(def.id)}','BanPlayer','${jsq(player.steamId)}')">Ban</button></div>`).join('') : '<div class="sw-empty">No players returned by RCON.</div>'}`;
}

async function serverWorkspacePlayerAction(id, command, payload) {
  if (command === 'BanPlayer' && !confirm('Ban this player?')) return;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/rcon/command`, { method: 'POST', body: JSON.stringify({ command, payload }) });
    toast(result.success ? (result.output || command + ' sent') : (result.error || 'RCON failed'), result.success ? 'ok' : 'err');
    serverWorkspaceRefreshTab('players');
  } catch (e) { toast('RCON failed: ' + e.message, 'err'); }
}
window.serverWorkspacePlayerAction = serverWorkspacePlayerAction;

function renderServerMods(def, body) {
  body.innerHTML = `<div class="sw-section"><h3>Mods</h3><div class="muted tiny" style="margin-bottom:14px">The existing game-specific mod manager opens from here. Unsupported games do not get this tab.</div><button class="btn primary" onclick="openMods('${jsq(def.id)}')">Open mod manager</button></div>`;
}

async function renderServerBackups(def, body) {
  const list = await api(`/api/servers/${encodeURIComponent(def.id)}/backups`);
  body.innerHTML = `<div class="sw-toolbar"><div><b>Backups</b><div class="tiny muted">Save snapshots managed by GameServerControl.</div></div><div class="spacer"></div><button class="btn primary" onclick="serverWorkspaceBackup('${jsq(def.id)}')">Backup now</button></div>${list?.length ? list.map(backup => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(backup.checkpointName)}</div><div class="sw-row-meta">${new Date(backup.createdAt).toLocaleString()} · ${Math.round((backup.sizeBytes || 0) / 1024 / 1024)} MB</div></div><button class="btn" onclick="serverWorkspaceRestore('${jsq(def.id)}','${encodeURIComponent(backup.checkpointName)}')">Restore</button><button class="btn danger" onclick="serverWorkspaceDeleteBackup('${jsq(def.id)}','${encodeURIComponent(backup.checkpointName)}')">Delete</button></div>`).join('') : '<div class="sw-empty">No backups yet.</div>'}`;
}

async function serverWorkspaceBackup(id) { await action(id, 'backup'); serverWorkspaceRefreshTab('backups'); }
async function serverWorkspaceRestore(id, name) {
  if (!confirm('Restore this backup? Stop the server first.')) return;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/backups/${name}/restore`, { method: 'POST' });
    toast(result.success ? result.message : (result.message || 'Restore failed'), result.success ? 'ok' : 'err');
    serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Restore failed: ' + e.message, 'err'); }
}
async function serverWorkspaceDeleteBackup(id, name) {
  if (!confirm('Delete this backup?')) return;
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/backups/${name}`, { method: 'DELETE' });
    toast('Backup deleted.', 'ok');
    serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Delete failed: ' + e.message, 'err'); }
}
window.serverWorkspaceBackup = serverWorkspaceBackup;
window.serverWorkspaceRestore = serverWorkspaceRestore;
window.serverWorkspaceDeleteBackup = serverWorkspaceDeleteBackup;

function renderServerNetwork(def, body) {
  const profile = profileFor(def);
  const ports = effectiveNetworkPorts(def);
  body.innerHTML = `<div class="sw-toolbar"><div><b>Network</b><div class="tiny muted">Port reservations and Windows Firewall metadata.</div></div><div class="spacer"></div>${profile?.ports?.length ? `<button class="btn" onclick="serverWorkspaceResetNetwork('${jsq(def.id)}')">Profile defaults</button>` : ''}<button class="btn primary" onclick="serverWorkspaceSaveNetwork('${jsq(def.id)}')">Save</button></div><div class="field"><label>Reserved/listening ports — one per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:220px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Example: UDP 2456-2457 Game or TCP 7777 Game/API.</div></div>`;
}
function serverWorkspaceResetNetwork(id) {
  const def = SERVERS.find(x => x.id === id), profile = def && profileFor(def), textarea = $('#swNetworkPorts');
  if (textarea) textarea.value = (profile?.ports || []).map(formatNetworkPort).join('\n');
}
async function serverWorkspaceSaveNetwork(id) {
  const def = SERVERS.find(x => x.id === id);
  if (!def) return;
  let ports;
  try { ports = parseNetworkPorts($('#swNetworkPorts')?.value || ''); }
  catch (e) { return toast(e.message, 'err'); }
  try {
    await api(`/api/servers/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify({ ...def, networkPorts: ports }) });
    toast('Network settings saved.', 'ok');
    await loadServers();
  } catch (e) { toast('Network settings rejected: ' + e.message, 'err'); }
}
window.serverWorkspaceResetNetwork = serverWorkspaceResetNetwork;
window.serverWorkspaceSaveNetwork = serverWorkspaceSaveNetwork;

async function renderServerSchedule(def, body) {
  const schedule = await api(`/api/servers/${encodeURIComponent(def.id)}/schedule`).catch(() => ({}));
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  body.innerHTML = `<div class="sw-toolbar"><div><b>Schedule</b><div class="tiny muted">Automated restart, update and backup maintenance.</div></div><div class="spacer"></div><button class="btn danger" onclick="serverWorkspaceClearSchedule('${jsq(def.id)}')">Clear</button><button class="btn primary" onclick="serverWorkspaceSaveSchedule('${jsq(def.id)}')">Save</button></div><div class="sw-section"><div class="checkrow"><input id="swRestart" type="checkbox" ${schedule.dailyRestartEnabled ? 'checked' : ''}><label for="swRestart">Daily restart</label></div><div class="field"><label>Hour (0–23)</label><input id="swRestartHour" type="number" min="0" max="23" value="${schedule.dailyRestartHour ?? 5}"></div></div><div class="sw-section"><div class="checkrow"><input id="swUpdate" type="checkbox" ${schedule.weeklyUpdateEnabled ? 'checked' : ''}><label for="swUpdate">Weekly update</label></div><div class="formgrid"><div class="field"><label>Day</label><select id="swDay">${days.map(day => `<option ${day === (schedule.weeklyUpdateDay || 'Wednesday') ? 'selected' : ''}>${day}</option>`).join('')}</select></div><div class="field"><label>Hour (0–23)</label><input id="swUpdateHour" type="number" min="0" max="23" value="${schedule.weeklyUpdateHour ?? 4}"></div></div></div><div class="sw-section"><div class="checkrow"><input id="swBackup" type="checkbox" ${schedule.hourlyBackupEnabled ? 'checked' : ''}><label for="swBackup">Hourly backup</label></div><div class="field"><label>Minute (0–59)</label><input id="swBackupMinute" type="number" min="0" max="59" value="${schedule.hourlyBackupMinute ?? 0}"></div></div>`;
}

async function serverWorkspaceSaveSchedule(id) {
  const body = {
    dailyRestartEnabled: $('#swRestart').checked,
    dailyRestartHour: Number($('#swRestartHour').value || 5),
    weeklyUpdateEnabled: $('#swUpdate').checked,
    weeklyUpdateDay: $('#swDay').value,
    weeklyUpdateHour: Number($('#swUpdateHour').value || 4),
    hourlyBackupEnabled: $('#swBackup').checked,
    hourlyBackupMinute: Number($('#swBackupMinute').value || 0)
  };
  try { await api(`/api/servers/${encodeURIComponent(id)}/schedule`, { method: 'POST', body: JSON.stringify(body) }); toast('Schedule saved.', 'ok'); }
  catch (e) { toast('Schedule failed: ' + e.message, 'err'); }
}
async function serverWorkspaceClearSchedule(id) {
  if (!confirm('Clear all maintenance scheduling for this server?')) return;
  try { await api(`/api/servers/${encodeURIComponent(id)}/schedule`, { method: 'DELETE' }); toast('Schedule cleared.', 'ok'); serverWorkspaceRefreshTab('schedule'); }
  catch (e) { toast('Clear schedule failed: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveSchedule = serverWorkspaceSaveSchedule;
window.serverWorkspaceClearSchedule = serverWorkspaceClearSchedule;

function renderServerSettings(def, body) {
  const profile = profileFor(def);
  body.innerHTML = `<div class="sw-toolbar"><div><b>Server settings</b><div class="tiny muted">Profile-aware defaults first; raw launch fields stay under Advanced.</div></div><div class="spacer"></div><button class="btn" onclick="openEdit('${jsq(def.id)}')">Edit advanced settings</button></div><div class="sw-info"><div>Profile</div><div>${esc(profile?.label || 'Custom')}</div><div>Run on</div><div>${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This machine'}</div><div>Server folder</div><div class="mono">${esc(def.guestWorkingDir || '')}</div><div>Executable</div><div class="mono">${esc(def.guestExePath || '')}</div><div>Steam App ID</div><div>${esc(def.steamAppId || '—')}</div><div>Log file</div><div class="mono">${esc(def.logPathInGuest || '—')}</div></div><div class="section">Maintenance</div><div class="sw-toolbar"><button class="btn" onclick="action('${jsq(def.id)}','update')">Update server</button>${profile?.caps?.config ? `<button class="btn" onclick="action('${jsq(def.id)}','apply')">Apply config</button>` : ''}<div class="spacer"></div><button class="btn danger" onclick="deleteServer('${jsq(def.id)}')">Delete / remove server</button></div>`;
}

function serverWorkspaceRefreshTab(tab = SERVER_WORKSPACE.tab) {
  const def = SERVERS.find(x => x.id === SERVER_WORKSPACE.serverId);
  if (def) renderServerWorkspaceTab(def, tab);
}
window.serverWorkspaceRefreshTab = serverWorkspaceRefreshTab;

async function createServerStatusConnection(onMessage) {
  const negotiate = await fetch(CFG.url + '/hubs/status/negotiate?negotiateVersion=1', {
    method: 'POST',
    headers: { Authorization: 'Bearer ' + CFG.token }
  });
  if (!negotiate.ok) throw new Error('SignalR negotiate HTTP ' + negotiate.status);

  const negotiated = await negotiate.json();
  const url = new URL(CFG.url);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.pathname = url.pathname.replace(/\/$/, '') + '/hubs/status';
  url.search = '';
  url.searchParams.set('id', negotiated.connectionToken);
  url.searchParams.set('access_token', CFG.token);

  const ws = new WebSocket(url.toString());
  const pending = new Map();
  let sequence = 0;
  let ping = null;

  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('WebSocket connection timed out.')), 7000);

    ws.onerror = () => {
      clearTimeout(timer);
      reject(new Error('WebSocket connection failed.'));
    };

    ws.onopen = () => {
      ws.send('{"protocol":"json","version":1}\u001e');
    };

    ws.onmessage = event => {
      for (const raw of String(event.data).split('\u001e')) {
        if (!raw) continue;
        try {
          const message = JSON.parse(raw);
          if (message.type == null) {
            clearTimeout(timer);
            resolve();
            continue;
          }
          if (message.type === 3 && message.invocationId && pending.has(message.invocationId)) {
            const request = pending.get(message.invocationId);
            pending.delete(message.invocationId);
            if (message.error) request.reject(new Error(message.error));
            else request.resolve(message.result);
            continue;
          }
          onMessage?.(message);
        } catch { }
      }
    };
  });

  ping = setInterval(() => {
    if (ws.readyState === WebSocket.OPEN) ws.send('{"type":6}\u001e');
  }, 15000);

  return {
    invoke(target, args = []) {
      return new Promise((resolve, reject) => {
        if (ws.readyState !== WebSocket.OPEN) return reject(new Error('Console connection is closed.'));
        const invocationId = String(++sequence);
        pending.set(invocationId, { resolve, reject });
        ws.send(JSON.stringify({ type: 1, invocationId, target, arguments: args }) + '\u001e');
        setTimeout(() => {
          if (!pending.has(invocationId)) return;
          pending.delete(invocationId);
          reject(new Error('Console request timed out.'));
        }, 10000);
      });
    },
    close() {
      if (ping) clearInterval(ping);
      for (const request of pending.values()) request.reject(new Error('Connection closed.'));
      pending.clear();
      try { ws.close(); } catch { }
    }
  };
}

function appendServerConsole(line) {
  const terminal = $('#swTerminal');
  if (!terminal) return;
  const at = line.at ?? line.At;
  const text = line.text ?? line.Text ?? '';
  const source = String(line.source ?? line.Source ?? '');
  const prefix = at ? `[${new Date(at).toLocaleTimeString()}] ` : '';
  terminal.textContent += (terminal.textContent ? '\n' : '') + prefix + (source === 'stderr' ? '[ERR] ' : '') + text;
  if (terminal.textContent.length > 180000) terminal.textContent = terminal.textContent.slice(-150000);
  terminal.scrollTop = terminal.scrollHeight;
}

async function renderServerConsole(def, body) {
  serverWorkspaceCleanupConsole();
  const profile = profileFor(def);
  const mode = profile?.caps?.console;
  if (!mode) {
    body.innerHTML = '<div class="sw-empty">No live console integration is available for this game.</div>';
    return;
  }

  const isRcon = mode === 'rcon';
  const isStdin = mode === 'stdin';
  const readOnly = mode === 'log';
  const rconReady = !!def.rconPort && !!def.rconPassword;

  body.innerHTML = `<div class="sw-console"><div class="sw-console-head"><span class="sw-console-dot"></span><span>${isRcon ? 'RCON console' : isStdin ? 'Interactive process console' : 'Live log'}</span><span class="spacer"></span><span>${readOnly ? 'read only' : isRcon ? esc((def.rconHost || 'auto') + ':' + (def.rconPort || '—')) : 'stdin / stdout'}</span></div><pre id="swTerminal" class="sw-terminal"></pre>${readOnly ? '' : `<div class="sw-consolebar"><input id="swConsoleInput" autocomplete="off" placeholder="Type a server command…" ${isRcon && !rconReady ? 'disabled' : ''} onkeydown="if(event.key==='Enter')serverWorkspaceSendConsole('${jsq(def.id)}')"><button id="swConsoleSend" class="btn primary" ${isRcon && !rconReady ? 'disabled' : ''} onclick="serverWorkspaceSendConsole('${jsq(def.id)}')">Send</button></div>`}<div id="swConsoleHelp" class="sw-help">${readOnly ? esc(def.logPathInGuest || 'Following configured server log') : isRcon ? (rconReady ? 'Commands are sent through the game RCON handler.' : 'Configure RCON port/password in Settings first.') : 'Waiting for attached console state…'}</div></div>`;

  const terminal = $('#swTerminal');
  if (isRcon) {
    terminal.textContent = rconReady ? 'RCON ready. Type a command below.' : 'RCON is not configured for this server.';
    return;
  }
  if (isStdin && def.hostingMode === 'Vm') {
    terminal.textContent = 'Live process console is currently available only for servers running on this machine.';
    const input = $('#swConsoleInput'), send = $('#swConsoleSend');
    if (input) input.disabled = true;
    if (send) send.disabled = true;
    return;
  }

  const connection = await createServerStatusConnection(message => {
    if (message.type !== 1) return;
    const target = String(message.target || '').toLowerCase();
    if (target === 'logline') {
      const line = message.arguments?.[0];
      const serverId = line?.serverId ?? line?.ServerId;
      if (line && serverId === def.id) appendServerConsole(line);
    } else if (target === 'consolestate') {
      const state = message.arguments?.[0];
      const serverId = state?.serverId ?? state?.ServerId;
      if (!state || serverId !== def.id) return;
      const available = state.inputAvailable ?? state.InputAvailable ?? false;
      const help = $('#swConsoleHelp'), input = $('#swConsoleInput'), send = $('#swConsoleSend');
      if (help) help.textContent = state.message ?? state.Message ?? '';
      if (input) input.disabled = !available;
      if (send) send.disabled = !available;
    }
  });

  if (readOnly) {
    await api(`/api/servers/${encodeURIComponent(def.id)}/logs/tail`, { method: 'POST', body: JSON.stringify({ action: 'start' }) });
    SERVER_WORKSPACE.console = {
      close() {
        connection.close();
        api(`/api/servers/${encodeURIComponent(def.id)}/logs/tail`, { method: 'POST', body: JSON.stringify({ action: 'stop' }) }).catch(() => {});
      }
    };
  } else {
    SERVER_WORKSPACE.console = connection;
    await connection.invoke('SubscribeConsole', [def.id]);
  }
}

async function serverWorkspaceSendConsole(id) {
  const def = SERVERS.find(x => x.id === id);
  const profile = def && profileFor(def);
  const input = $('#swConsoleInput');
  const command = input?.value.trim();
  if (!def || !command) return;
  input.value = '';

  if (profile?.caps?.console === 'rcon') {
    appendServerConsole({ at: new Date().toISOString(), source: 'stdin', text: '> ' + command });
    try {
      const result = await api(`/api/servers/${encodeURIComponent(id)}/rcon/command`, { method: 'POST', body: JSON.stringify({ command: 'Raw', payload: command }) });
      appendServerConsole({ at: new Date().toISOString(), source: result.success ? 'stdout' : 'stderr', text: result.success ? (result.output || '[OK]') : (result.error || '[Failed]') });
    } catch (e) { appendServerConsole({ at: new Date().toISOString(), source: 'stderr', text: e.message }); }
    return;
  }

  if (profile?.caps?.console === 'stdin') {
    try { await SERVER_WORKSPACE.console?.invoke('SendConsoleInput', [id, command]); }
    catch (e) { appendServerConsole({ at: new Date().toISOString(), source: 'stderr', text: e.message }); }
  }
}
window.serverWorkspaceSendConsole = serverWorkspaceSendConsole;

serverWorkspaceParseHash();
window.addEventListener('hashchange', () => {
  serverWorkspaceCleanupConsole();
  serverWorkspaceParseHash();
  if (CURRENT_VIEW !== 'servers') SERVER_WORKSPACE_BASE_SHOW_VIEW('servers');
  renderServers();
});
window.addEventListener('DOMContentLoaded', installServerWorkspaceStyles);

/* Network/schedule polish layered over the workspace so the profile remains the source of truth. */
function serverWorkspacePortCount(ports) {
  return (ports || []).reduce((sum, port) => {
    const start = Number(port.port);
    const end = port.endPort == null ? start : Number(port.endPort);
    return sum + (Number.isFinite(start) && Number.isFinite(end) && end >= start ? end - start + 1 : 0);
  }, 0);
}

function serverWorkspaceLooksWindows(def) {
  return /^[A-Za-z]:[\\/]/.test(def?.guestExePath || '');
}

function serverWorkspaceLinuxFirewallCommands(ports) {
  if (!(ports || []).length) return '';
  const ufw = ports.map(port => {
    const proto = String(port.protocol || '').toLowerCase();
    const start = Number(port.port), end = port.endPort == null ? start : Number(port.endPort);
    const range = end === start ? String(start) : `${start}:${end}`;
    return `sudo ufw allow ${range}/${proto}`;
  }).join('\n');
  const firewalld = ports.map(port => {
    const proto = String(port.protocol || '').toLowerCase();
    const start = Number(port.port), end = port.endPort == null ? start : Number(port.endPort);
    const range = end === start ? String(start) : `${start}-${end}`;
    return `sudo firewall-cmd --permanent --add-port=${range}/${proto}`;
  }).join('\n');
  return `# UFW\n${ufw}\n\n# firewalld\n${firewalld}\nsudo firewall-cmd --reload`;
}

renderServerNetwork = function (def, body) {
  const profile = profileFor(def);
  const ports = effectiveNetworkPorts(def);
  const profilePorts = profile?.ports || [];
  const portCount = serverWorkspacePortCount(ports);
  const profileCount = serverWorkspacePortCount(profilePorts);
  const isVm = def.hostingMode === 'Vm';
  const windows = serverWorkspaceLooksWindows(def);
  const exeRule = windows && /\.exe$/i.test(def.guestExePath || '');
  const running = serverWorkspaceStatus(def.id).processState === 'Running';

  let firewallInfo;
  if (isVm) {
    firewallInfo = '<div class="callout info"><b>Firewall:</b> This server runs inside Hyper-V. The host Agent does not alter the guest firewall; allow the listed ports inside the VM and forward/NAT them to the VM when required.</div>';
  } else if (exeRule) {
    firewallInfo = `<div class="callout info"><b>Windows Firewall:</b> GameServerControl allows the complete server executable while the server is running and removes its managed rules when it stops. Current lifecycle state: <b>${running ? 'rule active / server running' : 'rule closed / server stopped'}</b>.<br><span class="mono">${esc(def.guestExePath)}</span></div>`;
  } else if (windows) {
    firewallInfo = '<div class="callout info"><b>Windows Firewall:</b> This server launches through a script/JAR rather than a direct .exe, so GameServerControl does not create a program rule automatically. Use the ports below for a manual rule.</div>';
  } else {
    const commands = serverWorkspaceLinuxFirewallCommands(ports);
    firewallInfo = `<div class="callout info"><b>Linux firewall:</b> the Agent intentionally runs unprivileged, so it does not modify UFW/firewalld. Open only the game ports you need on the host/router.${commands ? `<pre class="mono" style="white-space:pre-wrap;margin:10px 0 0">${esc(commands)}</pre>` : ''}</div>`;
  }

  const rconInfo = profile?.caps?.rcon && def.rconPort
    ? `<div class="callout info"><b>RCON:</b> ${esc(def.rconHost || 'auto')}:${esc(def.rconPort)} is a separate administration endpoint. If you expose it beyond localhost/private LAN, add the matching firewall/NAT rule deliberately and protect it with a strong password.</div>`
    : '';
  const portRows = ports.length
    ? ports.map(port => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(port.name || 'Game port')}</div><div class="sw-row-meta mono">${esc(formatNetworkPort(port))}</div></div></div>`).join('')
    : '<div class="sw-empty">This profile has no curated network ports yet.</div>';

  body.innerHTML = `<div class="sw-toolbar"><div><b>Network</b><div class="tiny muted">Game profile ports, collision reservations and firewall behavior.</div></div></div><div class="sw-metrics"><div class="sw-metric"><span>Profile</span><b>${esc(profile?.label || 'Custom')}</b></div><div class="sw-metric"><span>Reservations</span><b>${ports.length}</b></div><div class="sw-metric"><span>Game ports</span><b>${portCount}</b></div><div class="sw-metric"><span>Profile defaults</span><b>${profileCount}</b></div></div>${firewallInfo}${rconInfo}<div class="sw-section"><h3>Effective game ports</h3><div class="tiny muted" style="margin-bottom:10px">These reservations prevent conflicting BareMetal servers on the same Agent and tell you what to allow/forward. Hyper-V guests have independent network stacks.</div>${portRows}</div><details class="advanced"><summary>Advanced port override</summary><div class="advanced-body"><div class="field"><label>One port or range per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:180px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Format: UDP 2456-2457 Game or TCP 7777 Game/API. Keep this aligned with the actual game configuration.</div></div><div class="sw-toolbar">${profilePorts.length ? `<button class="btn" onclick="serverWorkspaceResetNetwork('${jsq(def.id)}')">Reset to ${esc(profile.label)} defaults</button>` : ''}<div class="spacer"></div><button class="btn primary" onclick="serverWorkspaceSaveNetwork('${jsq(def.id)}')">Save override</button></div></div></details>`;
};

renderServerSchedule = async function (def, body) {
  const schedule = (await api(`/api/servers/${encodeURIComponent(def.id)}/schedule`).catch(() => null)) || {};
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  const linux = def.hostingMode !== 'Vm' && !serverWorkspaceLooksWindows(def);
  const platformInfo = linux ? '<div class="callout info">Dashboard maintenance scheduling currently uses Windows Task Scheduler. On Linux use systemd timers or cron to call the local GameServerControl API.</div>' : '';
  body.innerHTML = `${platformInfo}<div class="sw-toolbar"><div><b>Schedule</b><div class="tiny muted">Automated restart, update and backup maintenance.</div></div><div class="spacer"></div><button class="btn danger" onclick="serverWorkspaceClearSchedule('${jsq(def.id)}')">Clear</button><button class="btn primary" ${linux ? 'disabled' : ''} onclick="serverWorkspaceSaveSchedule('${jsq(def.id)}')">Save</button></div><div class="sw-section"><div class="checkrow"><input id="swRestart" type="checkbox" ${schedule.dailyRestartEnabled ? 'checked' : ''}><label for="swRestart">Daily restart</label></div><div class="field"><label>Hour (0–23)</label><input id="swRestartHour" type="number" min="0" max="23" value="${schedule.dailyRestartHour ?? 5}"></div></div><div class="sw-section"><div class="checkrow"><input id="swUpdate" type="checkbox" ${schedule.weeklyUpdateEnabled ? 'checked' : ''}><label for="swUpdate">Weekly update</label></div><div class="formgrid"><div class="field"><label>Day</label><select id="swDay">${days.map(day => `<option ${day === (schedule.weeklyUpdateDay || 'Wednesday') ? 'selected' : ''}>${day}</option>`).join('')}</select></div><div class="field"><label>Hour (0–23)</label><input id="swUpdateHour" type="number" min="0" max="23" value="${schedule.weeklyUpdateHour ?? 4}"></div></div></div><div class="sw-section"><div class="checkrow"><input id="swBackup" type="checkbox" ${schedule.hourlyBackupEnabled ? 'checked' : ''}><label for="swBackup">Hourly backup</label></div><div class="field"><label>Minute (0–59)</label><input id="swBackupMinute" type="number" min="0" max="59" value="${schedule.hourlyBackupMinute ?? 0}"></div></div>`;
};

const SERVER_WORKSPACE_BASE_OPEN_CREATE_SETTINGS = openCreateSettings;
openCreateSettings = function (key) {
  SERVER_WORKSPACE_BASE_OPEN_CREATE_SETTINGS(key);
  const game = GAMES.find(x => x.key === key);
  const summary = document.querySelector('#modalBody .profile-summary');
  if (!game || !summary) return;
  const ports = game.ports || [];
  const count = serverWorkspacePortCount(ports);
  const conflict = findNetworkConflict(ports);
  summary.insertAdjacentHTML('beforeend', `<div class="section">Network</div><div class="kv compact"><div>Port reservations</div><div>${ports.length} (${count} actual port${count === 1 ? '' : 's'})</div><div>Game ports</div><div class="mono">${ports.length ? ports.map(formatNetworkPort).map(esc).join('<br>') : 'No curated defaults yet'}</div><div>Windows firewall</div><div>${/\.exe$/i.test(game.exe || '') ? 'Whole server EXE is allowed only while the server is running.' : 'Manual rule may be required for script/JAR launchers.'}</div><div>Linux firewall</div><div>Ports are shown here; UFW/firewalld stays administrator-managed.</div></div>${conflict ? `<div class="callout"><b>Port conflict:</b> ${esc(formatNetworkPort(conflict.ours))} overlaps ${esc(conflict.server.name)} (${esc(formatNetworkPort(conflict.theirs))}). Change that server before installing/running both.</div>` : ''}`);
};

const SERVER_WORKSPACE_BASE_START_INSTALL = startInstall;
startInstall = async function (key) {
  const game = GAMES.find(x => x.key === key);
  const conflict = game ? findNetworkConflict(game.ports || []) : null;
  if (conflict) {
    const err = $('#cErr');
    if (err) {
      err.textContent = `Network conflict: ${formatNetworkPort(conflict.ours)} overlaps ${conflict.server.name} (${formatNetworkPort(conflict.theirs)}). Change the existing server ports first.`;
      err.classList.remove('hidden');
    }
    return;
  }
  return SERVER_WORKSPACE_BASE_START_INSTALL(key);
};
