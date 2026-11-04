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
  tabs.push(['overview', 'Overview'], ['diagnostics', 'Diagnostics']);
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
    root.innerHTML = `<div class="empty">No servers registered yet.<br><br><button class="btn primary" ${uiAction('openCreate')}>Create your first server</button> <button class="btn" ${uiAction('openDiscover')}>Discover installed servers</button> <button class="btn" ${uiAction('openAddExisting')}>Add existing server</button></div>`;
    return;
  }

  root.innerHTML = `<div class="sw-dashboard-head"><div><h2>Servers</h2><div class="muted tiny">Choose a server to open its console and controls.</div></div></div><div class="sw-server-grid">${SERVERS.map(def => {
    const status = serverWorkspaceStatus(def.id);
    const meta = serverMeta(def);
    const state = actionVisual(def.id, status.processState || 'Unknown');
    return `<button class="sw-server-card" ${uiAction('openServerPage',[def.id])}><div class="sw-card-head"><div class="sw-card-icon">${meta.icon}</div><div class="grow"><div class="sw-card-title">${esc(def.name)}</div><div class="sw-card-sub">${esc(meta.label)} · ${def.hostingMode === 'Vm' ? 'Hyper-V' : 'This machine'}</div></div><div class="sw-card-state ${serverWorkspaceStateClass(state)}">${esc(state)}</div></div><div class="sw-card-metrics"><span>CPU<b>${status.cpuPercent != null ? Number(status.cpuPercent).toFixed(1) + '%' : '—'}</b></span><span>RAM<b>${status.memoryMB != null ? status.memoryMB + ' MB' : '—'}</b></span><span>PID<b>${status.pidInGuest ?? '—'}</b></span></div></button>`;
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

  root.innerHTML = `<div class="sw-workspace" data-server-id="${esc(def.id)}" data-tab="${esc(SERVER_WORKSPACE.tab)}"><div class="sw-head"><button class="sw-back" ${uiAction('serverWorkspaceHome')} title="Back to servers">←</button><div class="sw-icon">${meta.icon}</div><div class="grow"><div class="sw-name">${esc(def.name)}</div><div class="sw-meta">${esc(meta.label)} · ${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This machine'}</div></div><div class="sw-actions"><button id="swStart" class="btn primary sw-power" ${uiAction('action',[def.id,'start'])}>Start</button><button id="swRestart" class="btn sw-power" ${uiAction('action',[def.id,'restart'])}>Restart</button><button id="swStop" class="btn danger sw-power" ${uiAction('action',[def.id,'stop'])}>Stop</button><span id="swState" class="sw-state"></span></div></div><div class="sw-tabs">${tabs.map(([key, label]) => `<button class="sw-tab ${SERVER_WORKSPACE.tab === key ? 'active' : ''}" ${uiAction('serverWorkspaceSetTab',[key])}>${label}</button>`).join('')}</div><div class="sw-body"><div id="swOperation" class="sw-operation"></div><div id="swTabBody"><div class="muted">Loading…</div></div></div></div>`;
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
    else if (tab === 'diagnostics') await renderServerDiagnostics(def, body);
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

function serverDiagnosticTime(at) {
  if (!at) return '—';
  const time = new Date(at);
  return Number.isFinite(time.getTime()) ? time.toLocaleString() : '—';
}

async function renderServerDiagnostics(def, body) {
  const result = await api(`/api/servers/${encodeURIComponent(def.id)}/diagnostics`);
  const d = result.diagnostics || {}, status = result.status || {};
  const operations = Object.entries(d.operations || {}).map(([kind, op]) =>
    `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(kind)} · ${esc(op.state || 'Unknown')}</div><div class="sw-row-meta">${esc(serverDiagnosticTime(op.finishedAt || op.startedAt))} · ${esc(op.message || '')}</div></div></div>`
  ).join('') || '<div class="sw-empty">No backup, update or restore operation observed in this Agent session.</div>';
  const events = (d.events || []).map(event =>
    `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(event.kind)} · ${esc(event.severity)}</div><div class="sw-row-meta">${esc(serverDiagnosticTime(event.at))} · ${esc(event.message || '')}</div></div></div>`
  ).join('') || '<div class="sw-empty">No events recorded in this Agent session.</div>';
  body.innerHTML = `<div class="sw-toolbar"><div><b>Server diagnostics</b><div class="tiny muted">Agent-observed events only. Recovery source may not be identifiable.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['diagnostics'])}>Refresh</button></div>
    <div class="sw-info"><div>Process</div><div>${esc(status.processState || 'Unknown')}</div><div>Last started</div><div>${esc(serverDiagnosticTime(status.startedAt))}</div><div>Last unexpected stop</div><div>${esc(serverDiagnosticTime(d.lastUnexpectedStopAt))}</div><div>Last recovery observed</div><div>${esc(serverDiagnosticTime(d.lastRecoveryObservedAt))}</div><div>Last manual restart</div><div>${esc(serverDiagnosticTime(d.lastManualRestartAt))}</div><div>Last backup</div><div>${esc(serverDiagnosticTime(status.lastBackupAt))}</div><div>Last error</div><div>${esc(status.lastError || '—')}</div></div>
    <div class="sw-section"><h3>Backup, update and restore</h3>${operations}</div>
    <div class="sw-section"><h3>Recent events</h3>${events}</div>
    <div class="tiny muted">${esc(result.note || '')} For persistent crash details, inspect the game and Agent logs.</div>`;
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
  body.innerHTML = `<div class="sw-toolbar"><div class="muted tiny">Game-specific settings from the ${esc(profileFor(def)?.label || def.gameType)} profile.</div><div class="spacer"></div><button class="btn primary" ${uiAction('serverWorkspaceSaveConfig',[def.id])}>Save configuration</button></div>${fields}`;
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
  body.innerHTML = `<div class="sw-toolbar"><div class="muted tiny">Players returned by the game RCON handler.</div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['players'])}>Refresh</button></div>${list?.length ? list.map(player => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(player.name)}</div><div class="sw-row-meta">${esc(player.steamId)} · ${esc(player.address || player.playerUid || '')}</div></div><button class="btn" ${uiAction('serverWorkspacePlayerAction',[def.id,'KickPlayer',player.steamId])}>Kick</button><button class="btn danger" ${uiAction('serverWorkspacePlayerAction',[def.id,'BanPlayer',player.steamId])}>Ban</button></div>`).join('') : '<div class="sw-empty">No players returned by RCON.</div>'}`;
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
  body.innerHTML = `<div class="sw-section"><h3>Mods</h3><div class="muted tiny" style="margin-bottom:14px">The existing game-specific mod manager opens from here. Unsupported games do not get this tab.</div><button class="btn primary" ${uiAction('openMods',[def.id])}>Open mod manager</button></div>`;
}

function serverWorkspaceBackupSize(bytes) {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n <= 0) return '—';
  if (n < 1024 * 1024) return `${Math.max(1, Math.round(n / 1024))} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(n < 10 * 1024 * 1024 ? 1 : 0)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function serverWorkspaceBackupKind(backup) {
  const kind = String(backup.kind || 'Archive');
  if (kind === 'SafetyArchive') return ['Safety archive', 'warn'];
  if (kind === 'SafetyCheckpoint') return ['Safety checkpoint', 'warn'];
  if (kind === 'HyperVCheckpoint') return ['Hyper-V checkpoint', 'up'];
  return ['Archive', 'up'];
}

async function renderServerBackups(def, body) {
  const [list, settings] = await Promise.all([
    api(`/api/servers/${encodeURIComponent(def.id)}/backups`),
    api(`/api/servers/${encodeURIComponent(def.id)}/backups/settings`)
  ]);
  const backups = list || [];
  const status = serverWorkspaceStatus(def.id);
  const restoreDisabled = def.hostingMode === 'Vm'
    ? String(status.vmState || '') !== 'Off'
    : String(status.processState || '') !== 'NotRunning';
  const normal = backups.filter(x => !String(x.kind || '').startsWith('Safety'));
  const safety = backups.filter(x => String(x.kind || '').startsWith('Safety'));
  const knownBytes = backups.reduce((sum, x) => sum + (Number(x.sizeBytes) || 0), 0);
  const last = backups[0];
  const restoreHint = def.hostingMode === 'Vm'
    ? 'Turn the Hyper-V VM fully off before restore.'
    : 'Stop the game server before restore.';

  const rows = backups.length ? backups.map(backup => {
    const [kindLabel, kindClass] = serverWorkspaceBackupKind(backup);
    const skipped = Number(backup.skippedFiles) || 0;
    const details = [
      new Date(backup.createdAt).toLocaleString(),
      serverWorkspaceBackupSize(backup.sizeBytes),
      backup.fileCount != null ? `${backup.fileCount} files` : null
    ].filter(Boolean).join(' · ');
    const title = backup.label || backup.checkpointName;
    return `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(title)}</div><div class="sw-row-meta mono">${esc(backup.checkpointName)}</div><div class="sw-row-meta">${esc(details)}</div>${skipped ? `<div class="tiny" style="color:var(--warn);margin-top:4px">Warning: backup.skippedFiles = ${skipped}; locked/unavailable files were skipped.</div>` : ''}</div><span class="state ${kindClass}">${esc(kindLabel)}</span><button class="btn" ${restoreDisabled || backup.restorable === false ? 'disabled' : ''} ${uiAction('serverWorkspaceRestore',[def.id,backup.checkpointName])}>Restore</button><button class="btn danger" ${uiAction('serverWorkspaceDeleteBackup',[def.id,backup.checkpointName])}>Delete</button></div>`;
  }).join('') : '<div class="sw-empty">No backups yet.</div>';

  body.innerHTML = `<div class="sw-toolbar"><div><b>Backups</b><div class="tiny muted">Archives/checkpoints stay on the server. Cloud access sends only control requests.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['backups'])}>Refresh</button></div><div class="sw-metrics"><div class="sw-metric"><span>Normal backups</span><b>${normal.length}</b></div><div class="sw-metric"><span>Safety copies</span><b>${safety.length}</b></div><div class="sw-metric"><span>Known storage</span><b>${serverWorkspaceBackupSize(knownBytes)}</b></div><div class="sw-metric"><span>Newest</span><b>${last ? new Date(last.createdAt).toLocaleString() : '—'}</b></div></div><div class="sw-section"><h3>Create backup</h3><div class="formgrid"><div class="field"><label>Backup name (optional)</label><input id="swBackupLabel" maxlength="80" placeholder="Before mod update"><div class="help">Human-readable label only. GameServerControl keeps the archive/checkpoint identifier machine-safe.</div></div><div class="field"><label>Retention</label><div style="display:flex;gap:8px"><input id="swBackupRetention" type="number" min="1" max="200" value="${settings.retention ?? 10}"><button class="btn" ${uiAction('serverWorkspaceSaveBackupRetention',[def.id])}>Save</button></div><div class="help">Keep ${settings.retention ?? 10} normal backups.${settings.isOverride ? ' Per-server override.' : ` Agent default: ${settings.defaultRetention ?? 10}.`} Safety copies are retained separately.</div></div></div><div class="sw-toolbar" style="margin-top:10px;margin-bottom:0"><div class="tiny muted">${esc(restoreHint)}</div><div class="spacer"></div><button class="btn primary" ${uiAction('serverWorkspaceCreateBackup',[def.id])}>Backup now</button></div></div><div class="callout info"><b>Safety copy:</b> before every restore, GameServerControl must create a complete pre-restore backup/checkpoint. If that safety copy fails, restore is aborted before current saves are replaced.${restoreDisabled ? `<br><b>Restore disabled:</b> ${esc(restoreHint)}` : ''}</div>${rows}`;
}

async function serverWorkspaceCreateBackup(id) {
  const label = $('#swBackupLabel')?.value.trim() || null;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/backups`, { method: 'POST', body: JSON.stringify({ label: label }) });
    toast(result.success ? result.message : (result.message || 'Backup failed'), result.success ? 'ok' : 'err');
    if (result.success) serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Backup failed: ' + e.message, 'err'); }
}

async function serverWorkspaceSaveBackupRetention(id) {
  const retention = Number($('#swBackupRetention')?.value);
  if (!Number.isInteger(retention) || retention < 1 || retention > 200) return toast('Retention must be between 1 and 200.', 'err');
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/backups/settings`, { method: 'PUT', body: JSON.stringify({ retention, isOverride: true, defaultRetention: retention }) });
    toast('Backup retention saved.', 'ok');
    serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Retention save failed: ' + e.message, 'err'); }
}

async function serverWorkspaceBackup(id) { await serverWorkspaceCreateBackup(id); }
async function serverWorkspaceRestore(id, name) {
  const def = SERVERS.find(x => x.id === id);
  const status = serverWorkspaceStatus(id);
  const restoreDisabled = def?.hostingMode === 'Vm'
    ? String(status.vmState || '') !== 'Off'
    : String(status.processState || '') !== 'NotRunning';
  if (restoreDisabled) return toast(def?.hostingMode === 'Vm' ? 'Turn the Hyper-V VM off before restore.' : 'Stop the server before restore.', 'err');
  if (!confirm(`Restore ${name}?\n\nSafety copy: GameServerControl will create a mandatory pre-restore backup/checkpoint first. If it fails, restore will not start.`)) return;
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/backups/${encodeURIComponent(name)}/restore`, { method: 'POST' });
    toast(result.success ? result.message : (result.message || 'Restore failed'), result.success ? 'ok' : 'err');
    serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Restore failed: ' + e.message, 'err'); }
}
async function serverWorkspaceDeleteBackup(id, name) {
  if (!confirm(`Delete backup ${name}?`)) return;
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/backups/${encodeURIComponent(name)}`, { method: 'DELETE' });
    toast('Backup deleted.', 'ok');
    serverWorkspaceRefreshTab('backups');
  } catch (e) { toast('Delete failed: ' + e.message, 'err'); }
}
window.serverWorkspaceCreateBackup = serverWorkspaceCreateBackup;
window.serverWorkspaceSaveBackupRetention = serverWorkspaceSaveBackupRetention;
window.serverWorkspaceBackup = serverWorkspaceBackup;
window.serverWorkspaceRestore = serverWorkspaceRestore;
window.serverWorkspaceDeleteBackup = serverWorkspaceDeleteBackup;

function renderServerNetwork(def, body) {
  const profile = profileFor(def);
  const ports = effectiveNetworkPorts(def);
  body.innerHTML = `<div class="sw-toolbar"><div><b>Network</b><div class="tiny muted">Port reservations and Windows Firewall metadata.</div></div><div class="spacer"></div>${profile?.ports?.length ? `<button class="btn" ${uiAction('serverWorkspaceResetNetwork',[def.id])}>Profile defaults</button>` : ''}<button class="btn primary" ${uiAction('serverWorkspaceSaveNetwork',[def.id])}>Save</button></div><div class="field"><label>Reserved/listening ports — one per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:220px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Example: UDP 2456-2457 Game or TCP 7777 Game/API.</div></div>`;
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
  body.innerHTML = `<div class="sw-toolbar"><div><b>Schedule</b><div class="tiny muted">Automated restart, update and backup maintenance.</div></div><div class="spacer"></div><button class="btn danger" ${uiAction('serverWorkspaceClearSchedule',[def.id])}>Clear</button><button class="btn primary" ${uiAction('serverWorkspaceSaveSchedule',[def.id])}>Save</button></div><div class="sw-section"><div class="checkrow"><input id="swRestart" type="checkbox" ${schedule.dailyRestartEnabled ? 'checked' : ''}><label for="swRestart">Daily restart</label></div><div class="field"><label>Hour (0–23)</label><input id="swRestartHour" type="number" min="0" max="23" value="${schedule.dailyRestartHour ?? 5}"></div></div><div class="sw-section"><div class="checkrow"><input id="swUpdate" type="checkbox" ${schedule.weeklyUpdateEnabled ? 'checked' : ''}><label for="swUpdate">Weekly update</label></div><div class="formgrid"><div class="field"><label>Day</label><select id="swDay">${days.map(day => `<option ${day === (schedule.weeklyUpdateDay || 'Wednesday') ? 'selected' : ''}>${day}</option>`).join('')}</select></div><div class="field"><label>Hour (0–23)</label><input id="swUpdateHour" type="number" min="0" max="23" value="${schedule.weeklyUpdateHour ?? 4}"></div></div></div><div class="sw-section"><div class="checkrow"><input id="swBackup" type="checkbox" ${schedule.hourlyBackupEnabled ? 'checked' : ''}><label for="swBackup">Hourly backup</label></div><div class="field"><label>Minute (0–59)</label><input id="swBackupMinute" type="number" min="0" max="59" value="${schedule.hourlyBackupMinute ?? 0}"></div></div>`;
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
  body.innerHTML = `<div class="sw-toolbar"><div><b>Server settings</b><div class="tiny muted">Profile-aware defaults first; raw launch fields stay under Advanced.</div></div><div class="spacer"></div><button class="btn" ${uiAction('openEdit',[def.id])}>Edit advanced settings</button></div><div class="sw-info"><div>Profile</div><div>${esc(profile?.label || 'Custom')}</div><div>Run on</div><div>${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This machine'}</div><div>Server folder</div><div class="mono">${esc(def.guestWorkingDir || '')}</div><div>Executable</div><div class="mono">${esc(def.guestExePath || '')}</div><div>Steam App ID</div><div>${esc(def.steamAppId || '—')}</div><div>Log file</div><div class="mono">${esc(def.logPathInGuest || '—')}</div></div><div class="section">Maintenance</div><div class="sw-toolbar"><button class="btn" ${uiAction('action',[def.id,'update'])}>Update server</button>${profile?.caps?.config ? `<button class="btn" ${uiAction('action',[def.id,'apply'])}>Apply config</button>` : ''}<div class="spacer"></div><button class="btn danger" ${uiAction('deleteServer',[def.id])}>Delete / remove server</button></div>
  <div class="sw-section"><h3>Discord notifications</h3><div id="swDiscordStatus" class="tiny muted">Loading notification settings…</div><div class="field"><label>Discord webhook (HTTPS)</label><input id="swDiscordUrl" type="password" class="mono" autocomplete="new-password" placeholder="Paste a new URL to set or replace; blank keeps current"><div class="help">The webhook is optional. Its URL is never shown in this settings panel.</div></div><div class="checkrow"><input id="swDiscordAlertsOnly" type="checkbox"><label for="swDiscordAlertsOnly">Only failures and unexpected stops (no routine start/stop/success messages)</label></div><div class="sw-toolbar"><button class="btn primary" ${uiAction('serverWorkspaceSaveNotifications',[def.id])}>Save notifications</button><button class="btn" ${uiAction('serverWorkspaceSendTestNotification',[def.id])}>Send test</button><button class="btn danger" ${uiAction('serverWorkspaceClearNotifications',[def.id])}>Disable</button></div><div class="tiny muted">Discord webhooks only send alerts. Remote commands require a separate secured Discord bot.</div></div>`;
  serverWorkspaceLoadNotifications(def.id);
}

async function serverWorkspaceLoadNotifications(id) {
  const status = $('#swDiscordStatus');
  if (!status) return;
  try {
    const data = await api(`/api/servers/${encodeURIComponent(id)}/notifications`);
    if (!$('#swDiscordStatus')) return;
    status.textContent = data.configured ? 'Discord webhook configured.' : 'Disabled — no webhook configured.';
    const only = $('#swDiscordAlertsOnly');
    if (only) only.checked = !!data.alertsOnly;
  } catch (e) {
    status.textContent = 'Unable to load Discord settings: ' + e.message;
  }
}
async function serverWorkspaceSaveNotifications(id) {
  try {
    const webhookUrl = $('#swDiscordUrl')?.value.trim() || null;
    const alertsOnly = !!$('#swDiscordAlertsOnly')?.checked;
    await api(`/api/servers/${encodeURIComponent(id)}/notifications`, {
      method: 'PUT', body: JSON.stringify({ webhookUrl, alertsOnly })
    });
    if ($('#swDiscordUrl')) $('#swDiscordUrl').value = '';
    toast('Discord notification settings saved.', 'ok');
    await serverWorkspaceLoadNotifications(id);
  } catch (e) { toast('Discord settings failed: ' + e.message, 'err'); }
}
async function serverWorkspaceSendTestNotification(id) {
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/notifications/test`, {method:'POST'});
    toast('Discord test notification sent.', 'ok');
  } catch (e) { toast('Discord test failed: ' + e.message, 'err'); }
}
async function serverWorkspaceClearNotifications(id) {
  if (!confirm('Disable Discord notifications for this server?')) return;
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/notifications`, {
      method: 'PUT', body: JSON.stringify({ disable: true })
    });
    if ($('#swDiscordUrl')) $('#swDiscordUrl').value = '';
    toast('Discord notifications disabled.', 'ok');
    await serverWorkspaceLoadNotifications(id);
  } catch (e) { toast('Discord settings failed: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveNotifications = serverWorkspaceSaveNotifications;
window.serverWorkspaceSendTestNotification = serverWorkspaceSendTestNotification;
window.serverWorkspaceClearNotifications = serverWorkspaceClearNotifications;

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

  body.innerHTML = `<div class="sw-console"><div class="sw-console-head"><span class="sw-console-dot"></span><span>${isRcon ? 'RCON console' : isStdin ? 'Interactive process console' : 'Live log'}</span><span class="spacer"></span><span>${readOnly ? 'read only' : isRcon ? esc((def.rconHost || 'auto') + ':' + (def.rconPort || '—')) : 'stdin / stdout'}</span></div><pre id="swTerminal" class="sw-terminal"></pre>${readOnly ? '' : `<div class="sw-consolebar"><input id="swConsoleInput" autocomplete="off" placeholder="Type a server command…" ${isRcon && !rconReady ? 'disabled' : ''} ${uiAction('serverWorkspaceSendConsole',[def.id],{event:'keydown',key:'Enter'})}><button id="swConsoleSend" class="btn primary" ${isRcon && !rconReady ? 'disabled' : ''} ${uiAction('serverWorkspaceSendConsole',[def.id])}>Send</button></div>`}<div id="swConsoleHelp" class="sw-help">${readOnly ? esc(def.logPathInGuest || 'Following configured server log') : isRcon ? (rconReady ? 'Commands are sent through the game RCON handler.' : 'Configure RCON port/password in Settings first.') : 'Waiting for attached console state…'}</div></div>`;

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

  await connection.invoke('SubscribeConsole', [def.id]);
  SERVER_WORKSPACE.console = connection;
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

  body.innerHTML = `<div class="sw-toolbar"><div><b>Network</b><div class="tiny muted">Game profile ports, collision reservations and firewall behavior.</div></div></div><div class="sw-metrics"><div class="sw-metric"><span>Profile</span><b>${esc(profile?.label || 'Custom')}</b></div><div class="sw-metric"><span>Reservations</span><b>${ports.length}</b></div><div class="sw-metric"><span>Game ports</span><b>${portCount}</b></div><div class="sw-metric"><span>Profile defaults</span><b>${profileCount}</b></div></div>${firewallInfo}${rconInfo}<div class="sw-section"><h3>Effective game ports</h3><div class="tiny muted" style="margin-bottom:10px">These reservations prevent conflicting BareMetal servers on the same Agent and tell you what to allow/forward. Hyper-V guests have independent network stacks.</div>${portRows}</div><details class="advanced"><summary>Advanced port override</summary><div class="advanced-body"><div class="field"><label>One port or range per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:180px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help">Format: UDP 2456-2457 Game or TCP 7777 Game/API. Keep this aligned with the actual game configuration.</div></div><div class="sw-toolbar">${profilePorts.length ? `<button class="btn" ${uiAction('serverWorkspaceResetNetwork',[def.id])}>Reset to ${esc(profile.label)} defaults</button>` : ''}<div class="spacer"></div><button class="btn primary" ${uiAction('serverWorkspaceSaveNetwork',[def.id])}>Save override</button></div></div></details>`;
};

renderServerSchedule = async function (def, body) {
  const schedule = (await api(`/api/servers/${encodeURIComponent(def.id)}/schedule`).catch(() => null)) || {};
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  const linux = def.hostingMode !== 'Vm' && !serverWorkspaceLooksWindows(def);
  const platformInfo = linux ? '<div class="callout info">Dashboard maintenance scheduling currently uses Windows Task Scheduler. On Linux use systemd timers or cron to call the local GameServerControl API.</div>' : '';
  body.innerHTML = `${platformInfo}<div class="sw-toolbar"><div><b>Schedule</b><div class="tiny muted">Automated restart, update and backup maintenance.</div></div><div class="spacer"></div><button class="btn danger" ${uiAction('serverWorkspaceClearSchedule',[def.id])}>Clear</button><button class="btn primary" ${linux ? 'disabled' : ''} ${uiAction('serverWorkspaceSaveSchedule',[def.id])}>Save</button></div><div class="sw-section"><div class="checkrow"><input id="swRestart" type="checkbox" ${schedule.dailyRestartEnabled ? 'checked' : ''}><label for="swRestart">Daily restart</label></div><div class="field"><label>Hour (0–23)</label><input id="swRestartHour" type="number" min="0" max="23" value="${schedule.dailyRestartHour ?? 5}"></div></div><div class="sw-section"><div class="checkrow"><input id="swUpdate" type="checkbox" ${schedule.weeklyUpdateEnabled ? 'checked' : ''}><label for="swUpdate">Weekly update</label></div><div class="formgrid"><div class="field"><label>Day</label><select id="swDay">${days.map(day => `<option ${day === (schedule.weeklyUpdateDay || 'Wednesday') ? 'selected' : ''}>${day}</option>`).join('')}</select></div><div class="field"><label>Hour (0–23)</label><input id="swUpdateHour" type="number" min="0" max="23" value="${schedule.weeklyUpdateHour ?? 4}"></div></div></div><div class="sw-section"><div class="checkrow"><input id="swBackup" type="checkbox" ${schedule.hourlyBackupEnabled ? 'checked' : ''}><label for="swBackup">Hourly backup</label></div><div class="field"><label>Minute (0–59)</label><input id="swBackupMinute" type="number" min="0" max="59" value="${schedule.hourlyBackupMinute ?? 0}"></div></div>`;
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

/* Workspace overrides externalized from index.html for CSP. */
/* Safe config-file workspace. The Agent API remains the filesystem authority. */
(() => {
  const state={serverId:null,list:[],selected:null,content:null,history:[],dirty:false,filter:'',validationTimer:null,validationSeq:0};
  window.GSC_FILES=state;

  const style=document.createElement('style');
  style.id='gscFilesEditorStyles';
  style.textContent=`
    .fse-layout{display:grid;grid-template-columns:minmax(250px,330px) minmax(0,1fr);gap:12px;min-height:520px}.fse-sidebar,.fse-main{border:1px solid var(--line);border-radius:9px;background:#101318;overflow:hidden}.fse-filter{padding:10px;border-bottom:1px solid var(--line)}.fse-filter input{width:100%}.fse-list{max-height:650px;overflow:auto;padding:8px}.fse-group{margin:10px 5px 5px;color:var(--muted);font-size:10px;font-weight:850;text-transform:uppercase;letter-spacing:.55px}.fse-file{display:block;width:100%;text-align:left;color:var(--text);background:transparent;border:1px solid transparent;border-radius:7px;padding:8px 9px;margin:2px 0;cursor:pointer}.fse-file:hover{background:#171c23;border-color:#2c3440}.fse-file.active{background:#1a2028;border-color:#536171}.fse-file-name{font-size:12px;font-weight:800;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.fse-file-meta{font-size:10px;color:var(--muted);margin-top:3px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.fse-file-badges{display:flex;gap:5px;margin-top:5px;flex-wrap:wrap}.fse-badge{font-size:9px;border:1px solid var(--line);border-radius:999px;padding:2px 6px;color:var(--muted)}.fse-badge.warn{color:var(--warn);border-color:rgba(210,153,34,.45)}.fse-main{padding:12px}.fse-head{display:flex;gap:8px;align-items:flex-start;margin-bottom:10px}.fse-title{font-size:14px;font-weight:850}.fse-path{font:10px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--muted);overflow-wrap:anywhere;margin-top:3px}.fse-head-actions{margin-left:auto;display:flex;gap:6px;flex-wrap:wrap}.fse-statusbar{display:flex;gap:7px;align-items:center;flex-wrap:wrap;margin-bottom:9px}.fse-status{font-size:10px;border:1px solid var(--line);border-radius:999px;padding:3px 7px;color:var(--muted)}.fse-status.ok{color:var(--ok);border-color:rgba(63,185,80,.45)}.fse-status.err{color:var(--danger);border-color:rgba(248,81,73,.55)}.fse-status.warn{color:var(--warn);border-color:rgba(210,153,34,.45)}.fse-editgrid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:9px}.fse-pane-title{font-size:10px;color:var(--muted);text-transform:uppercase;letter-spacing:.5px;margin:0 0 5px}.fse-editor{box-sizing:border-box;width:100%;min-height:390px;max-height:58vh;resize:vertical;background:#080b0f;color:#dce3ec;border:1px solid #303947;border-radius:7px;padding:11px;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace;tab-size:2}.fse-preview{box-sizing:border-box;min-height:390px;max-height:58vh;overflow:auto;background:#080b0f;color:#dce3ec;border:1px solid #303947;border-radius:7px;padding:11px;margin:0;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace;white-space:pre-wrap;word-break:break-word;tab-size:2}.fse-history{margin-top:12px;border-top:1px solid var(--line);padding-top:11px}.fse-history-row{display:flex;gap:8px;align-items:center;padding:7px 0;border-bottom:1px solid rgba(255,255,255,.05)}.fse-history-row:last-child{border-bottom:0}.fse-history-row .grow{min-width:0}.fse-token-comment{color:#7f8b99}.fse-token-string{color:#a5d6ff}.fse-token-number{color:#d2a8ff}.fse-token-literal{color:#ff7b72}.fse-token-key{color:#79c0ff}.fse-token-section{color:#ffa657;font-weight:700}.fse-token-tag{color:#7ee787}.fse-empty{padding:45px 20px;text-align:center;color:var(--muted)}
    @media(max-width:1000px){.fse-layout{grid-template-columns:1fr}.fse-list{max-height:260px}.fse-editgrid{grid-template-columns:1fr}.fse-preview{min-height:220px;max-height:360px}}
  `;
  document.head.appendChild(style);

  const baseTabs=serverWorkspaceTabs;
  serverWorkspaceTabs=function(def){
    const tabs=baseTabs(def);
    if(!tabs.some(x=>x[0]==='files')){
      const at=tabs.findIndex(x=>x[0]==='backups');
      tabs.splice(at<0?tabs.length:at,0,['files','Files']);
    }
    return tabs;
  };

  const baseRenderTab=renderServerWorkspaceTab;
  renderServerWorkspaceTab=async function(def,tab){
    if(tab!=='files')return baseRenderTab(def,tab);
    const token=++SERVER_WORKSPACE.renderToken,body=$('#swTabBody');
    if(!body)return;
    body.innerHTML='<div class="muted">Loading config files…</div>';
    try{await renderServerFiles(def,body);if(token!==SERVER_WORKSPACE.renderToken)return}
    catch(e){body.innerHTML=`<div class="callout">${esc(e.message)}</div>`}
  };

  function sizeText(bytes){
    const n=Number(bytes)||0;if(n<1024)return n+' B';if(n<1024*1024)return (n/1024).toFixed(n<10240?1:0)+' KB';return (n/1024/1024).toFixed(1)+' MB';
  }

  function fileOwner(file){
    const path=String(file.path||'').replace(/\\/g,'/'),parts=path.split('/').filter(Boolean),lower=parts.map(x=>x.toLowerCase());
    const patterns=[['bepinex','plugins'],['factorygame','mods'],['pal','binaries','win64','mods']];
    for(const pattern of patterns){
      for(let i=0;i<=lower.length-pattern.length;i++){
        if(pattern.every((p,j)=>lower[i+j]===p)&&parts[i+pattern.length])return parts[i+pattern.length];
      }
    }
    for(const root of ['plugins','mods']){const i=lower.indexOf(root);if(i>=0&&parts[i+1])return parts[i+1]}
    for(const root of ['config','configs']){const i=lower.indexOf(root);if(i>=0&&parts[i+2])return parts[i+1]}
    const bi=lower.indexOf('bepinex');
    if(bi>=0&&lower[bi+1]==='config'&&parts[bi+2])return parts[bi+2].replace(/\.[^.]+$/,'');
    if(String(file.group||'').toLowerCase().includes('mod'))return String(file.name||'').replace(/\.[^.]+$/,'');
    return '';
  }

  function groupLabel(file){const owner=fileOwner(file);return owner?`${file.group||'Config'} · ${owner}`:(file.group||'Config')}

  function renderSidebar(){
    const host=$('#fseList');if(!host)return;
    const q=state.filter.trim().toLowerCase();
    const files=state.list.filter(file=>!q||[file.name,file.path,file.group,fileOwner(file),file.format].some(v=>String(v||'').toLowerCase().includes(q)));
    if(!files.length){host.innerHTML='<div class="fse-empty">No matching config files.</div>';return}
    const groups=new Map();
    for(const file of files){const label=groupLabel(file);if(!groups.has(label))groups.set(label,[]);groups.get(label).push(file)}
    host.innerHTML=[...groups.entries()].sort((a,b)=>a[0].localeCompare(b[0])).map(([label,items])=>`<div class="fse-group">${esc(label)}</div>${items.sort((a,b)=>String(a.path).localeCompare(String(b.path))).map(file=>`<button class="fse-file ${state.selected===file.path?'active':''}" ${uiAction('gscFilesOpen',[state.serverId,file.path])}><div class="fse-file-name">${esc(file.name)}</div><div class="fse-file-meta">${esc(file.path)}</div><div class="fse-file-badges"><span class="fse-badge">${esc(String(file.format||'text').toUpperCase())}</span><span class="fse-badge">${esc(sizeText(file.sizeBytes))}</span>${file.restartRecommended?'<span class="fse-badge warn">restart</span>':''}</div></button>`).join('')}`).join('');
  }

  function tokenSpan(cls,value){return `<span class="${cls}">${esc(value)}</span>`}
  function tokenizedLine(line,format){
    const trimmed=line.trimStart();
    if(/^(#|;|\/\/)/.test(trimmed))return tokenSpan('fse-token-comment',line);
    if(format==='xml'||format==='config'){
      let out='',at=0;for(const match of line.matchAll(/<[^>]+>/g)){out+=esc(line.slice(at,match.index))+tokenSpan('fse-token-tag',match[0]);at=match.index+match[0].length}return out+esc(line.slice(at));
    }
    if(format==='json'||format==='jsonc'){
      const re=/"(?:\\.|[^"\\])*"|-?(?:\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b|\b(?:true|false|null)\b|\/\/.*$/g;let out='',at=0;
      for(const match of line.matchAll(re)){out+=esc(line.slice(at,match.index));const v=match[0];let cls='fse-token-literal';if(v.startsWith('//'))cls='fse-token-comment';else if(v.startsWith('"'))cls=/^\s*:/.test(line.slice(match.index+v.length))?'fse-token-key':'fse-token-string';else if(/^[-\d]/.test(v))cls='fse-token-number';out+=tokenSpan(cls,v);at=match.index+v.length}return out+esc(line.slice(at));
    }
    if(/^\s*\[.*\]\s*$/.test(line))return tokenSpan('fse-token-section',line);
    const separator=format==='yaml'?line.indexOf(':'):Math.max(line.indexOf('='),line.indexOf(':'));
    if(separator>0){const key=line.slice(0,separator),rest=line.slice(separator);return tokenSpan('fse-token-key',key)+esc(rest).replace(/\b(true|false|null)\b/g,'<span class="fse-token-literal">$1</span>')}
    return esc(line);
  }

  function highlight(content,format){
    const text=String(content||''),limit=140000,visible=text.length>limit?text.slice(0,limit):text;
    const rendered=visible.replace(/\r\n/g,'\n').replace(/\r/g,'\n').split('\n').map(line=>tokenizedLine(line,String(format||'').toLowerCase())).join('\n');
    return rendered+(text.length>limit?'\n\n<span class="fse-token-comment">[Highlighted preview truncated; the editor still contains the complete file.]</span>':'');
  }

  function currentInfo(){return state.list.find(x=>x.path===state.selected)||null}
  function setValidation(kind,text){const el=$('#fseValidation');if(!el)return;el.className='fse-status '+(kind||'');el.textContent=text}
  function updatePreview(){const input=$('#fseText'),preview=$('#fsePreview');if(!input||!preview)return;preview.innerHTML=highlight(input.value,state.content?.format||currentInfo()?.format);}

  function editorHtml(){
    const info=currentInfo(),c=state.content;
    if(!c||!info)return '<div class="fse-empty">Select a config file.</div>';
    const running=serverWorkspaceStatus(state.serverId).processState==='Running';
    const restart=info.restartRecommended?`<span class="fse-status warn">Restart recommended${running?' · server running':''}</span>`:'<span class="fse-status ok">Reload usually not required</span>';
    const history=state.history.length?state.history.map(x=>`<div class="fse-history-row"><div class="grow"><div class="fse-file-name">${new Date(x.createdAt).toLocaleString()}</div><div class="fse-file-meta">${esc(sizeText(x.sizeBytes))} · ${esc(x.id)}</div></div><button class="btn" ${uiAction('gscFilesRestore',[x.id])}>Restore</button></div>`).join(''):'<div class="muted tiny">No edit history yet. A snapshot is created automatically before every save or restore.</div>';
    return `<div class="fse-head"><div class="grow"><div class="fse-title">${esc(info.name)}</div><div class="fse-path">${esc(info.path)}</div></div><div class="fse-head-actions"><button class="btn" ${uiAction('gscFilesReload')}>Reload</button><button id="fseSave" class="btn primary" ${state.dirty?'':'disabled'} ${uiAction('gscFilesSave')}>Save</button></div></div><div class="fse-statusbar"><span class="fse-status">${esc(String(c.format||info.format||'text').toUpperCase())}</span><span class="fse-status">${esc(sizeText(c.sizeBytes))}</span><span class="fse-status">${new Date(c.modifiedAt).toLocaleString()}</span>${restart}<span id="fseValidation" class="fse-status">Unchanged</span></div>${info.restartRecommended&&running?'<div class="callout info">This file is normally read when the server/plugin starts. Save is allowed while running, but a restart is recommended for the change to take effect.</div>':''}<div class="fse-editgrid"><div><div class="fse-pane-title">Editor · Ctrl+S to save</div><textarea id="fseText" class="fse-editor" spellcheck="false" ${uiAction('gscFilesInput',[],{event:'input'})} ${uiAction('gscFilesKeydown',[],{event:'keydown',pass:'event'})}>${esc(c.content)}</textarea></div><div><div class="fse-pane-title">Syntax-highlighted preview</div><pre id="fsePreview" class="fse-preview">${highlight(c.content,c.format||info.format)}</pre></div></div><div class="fse-history"><div class="sw-toolbar"><div><b>Edit history</b><div class="tiny muted">Last ${state.history.length} snapshots for this file.</div></div><div class="spacer"></div></div>${history}</div>`;
  }

  function renderEditor(){const host=$('#fseMain');if(host)host.innerHTML=editorHtml()}

  window.gscFilesFilter=function(value){state.filter=String(value||'');renderSidebar()};

  window.gscFilesOpen=async function(serverId,path,force=false){
    if(state.dirty&&!force&&state.selected&&state.selected!==path&&!confirm('Discard unsaved changes in the current config file?'))return;
    state.serverId=serverId;state.selected=path;state.dirty=false;renderSidebar();
    const host=$('#fseMain');if(host)host.innerHTML='<div class="fse-empty">Loading file…</div>';
    try{
      const encoded=encodeURIComponent(path);const [content,history]=await Promise.all([api(`/api/servers/${encodeURIComponent(serverId)}/files/content?path=${encoded}`),api(`/api/servers/${encodeURIComponent(serverId)}/files/history?path=${encoded}`)]);
      if(state.selected!==path)return;state.content=content;state.history=history||[];renderEditor();
    }catch(e){if(host)host.innerHTML=`<div class="callout">${esc(e.message)}</div>`}
  };

  window.gscFilesInput=function(){
    if(!state.content)return;state.dirty=true;const save=$('#fseSave');if(save)save.disabled=false;updatePreview();setValidation('','Checking syntax…');
    clearTimeout(state.validationTimer);const seq=++state.validationSeq;
    state.validationTimer=setTimeout(async()=>{const input=$('#fseText');if(!input||seq!==state.validationSeq)return;try{await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/validate`,{method:'POST',body:JSON.stringify({path:state.selected,content:input.value})});if(seq===state.validationSeq)setValidation('ok','Syntax valid')}catch(e){if(seq===state.validationSeq)setValidation('err',e.message)}},650);
  };

  window.gscFilesKeydown=function(event){if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='s'){event.preventDefault();gscFilesSave()}};

  async function refreshList(){
    const list=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files`);state.list=list.files||[];renderSidebar();
  }
  async function refreshHistory(){state.history=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/history?path=${encodeURIComponent(state.selected)}`)||[];renderEditor()}

  window.gscFilesSave=async function(){
    const input=$('#fseText');if(!input||!state.content||!state.selected||!state.dirty)return;
    const save=$('#fseSave');if(save){save.disabled=true;save.textContent='Saving…'}
    try{
      const payload={path:state.selected,content:input.value};
      await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/validate`,{method:'POST',body:JSON.stringify(payload)});
      const result=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/content`,{method:'PUT',body:JSON.stringify({...payload,expectedSha256:state.content.sha256})});
      state.content={...state.content,content:input.value,sha256:result.sha256,sizeBytes:result.sizeBytes,modifiedAt:result.modifiedAt};state.dirty=false;setValidation('ok','Saved · syntax valid');
      await refreshList();await refreshHistory();const info=currentInfo();setNotice(info?.restartRecommended?'Config saved. Restart recommended for this change.':'Config saved.','ok',false);
    }catch(e){setValidation('err',e.message);setNotice('Config save failed: '+e.message,'err',false);if(save)save.disabled=false}
    finally{if(save)save.textContent='Save'}
  };

  window.gscFilesReload=function(){if(!state.selected)return;if(state.dirty&&!confirm('Discard unsaved changes and reload this file?'))return;gscFilesOpen(state.serverId,state.selected,true)};

  window.gscFilesRestore=async function(backupId){
    if(!state.selected||!state.content)return;if(!confirm('Restore this config snapshot? The current file will be saved as an undo snapshot first.'))return;
    try{
      await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/restore`,{method:'POST',body:JSON.stringify({path:state.selected,backupId,expectedSha256:state.content.sha256})});
      setNotice('Config snapshot restored. An undo snapshot was created.','ok',false);await refreshList();await gscFilesOpen(state.serverId,state.selected,true);
    }catch(e){setNotice('Restore failed: '+e.message,'err',false)}
  };

  window.renderServerFiles=async function(def,body){
    if(state.serverId!==def.id){clearTimeout(state.validationTimer);state.serverId=def.id;state.list=[];state.selected=null;state.content=null;state.history=[];state.dirty=false;state.filter=''}
    const result=await api(`/api/servers/${encodeURIComponent(def.id)}/files`);
    if(!result.supported){body.innerHTML=`<div class="callout info"><b>Files editor unavailable.</b><br>${esc(result.reason||'This server does not support safe config-file access.')}</div>`;return}
    state.list=result.files||[];
    if(state.selected&&!state.list.some(x=>x.path===state.selected)){state.selected=null;state.content=null;state.history=[];state.dirty=false}
    if(!state.selected&&state.list.length)state.selected=state.list[0].path;
    body.innerHTML=`<div class="sw-toolbar"><div><b>Config files</b><div class="tiny muted">Only existing text configs inside allowlisted server/mod roots are exposed. Arbitrary filesystem access is not available.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['files'])}>Rescan</button></div><div class="fse-layout"><aside class="fse-sidebar"><div class="fse-filter"><input placeholder="Filter files, paths or plugins…" value="${esc(state.filter)}" ${uiAction('gscFilesFilter',[],{event:'input',pass:'value'})}></div><div id="fseList" class="fse-list"></div></aside><main id="fseMain" class="fse-main"></main></div>`;
    renderSidebar();renderEditor();if(state.selected&&!state.content)await gscFilesOpen(def.id,state.selected,true);
  };
})();

/* Parallel install polling + live detail log. */
refreshJobs=async function(render=true){
  const refs=loadJobRefs();let active=0;
  await Promise.all(refs.map(async r=>{try{JOB_CACHE[r.jobId]=await api('/api/servers/install/'+encodeURIComponent(r.jobId));if(!JOB_CACHE[r.jobId].finished)active++}catch{JOB_CACHE[r.jobId]={jobId:r.jobId,phase:'expired',line:'Job no longer available (Agent may have restarted).',finished:true,success:false,logLines:[]}}}));
  const badge=$('#installBadge');if(badge){badge.textContent=active;badge.classList.toggle('hidden',active===0)}if(render||CURRENT_VIEW==='installs')renderJobs();
};

async function updateLiveJobDetail(id){
  try{const p=await api('/api/servers/install/'+encodeURIComponent(id));JOB_CACHE[id]=p;const phase=$('#liveJobPhase'),bar=$('#liveJobBar'),line=$('#liveJobLine'),log=$('#liveJobLog'),state=$('#liveJobState');if(!log)return p;const nearBottom=log.scrollHeight-log.scrollTop-log.clientHeight<50;if(phase)phase.textContent=phaseText(p.phase||'');if(bar)bar.style.width=Math.max(0,Math.min(100,p.percentHint??0))+'%';if(line)line.textContent=p.line||'';if(state){state.textContent=p.finished?(p.success?'DONE':'FAILED'):'ACTIVE';state.className='state '+(p.success?'up':p.finished?'down':'warn')}log.textContent=(p.logLines||[]).join('\n')||'Waiting for SteamCMD output…';if(nearBottom)log.scrollTop=log.scrollHeight;return p}catch(e){const line=$('#liveJobLine');if(line)line.textContent='Log refresh failed: '+e.message;return null}}
openJob=async function(id){
  const r=loadJobRefs().find(x=>x.jobId===id)||{jobId:id};
  openModal(r.name||id,`<div class="install-live-head"><div><div class="jobphase">Job ${esc(id)} · <span id="liveJobPhase">Loading</span></div><div id="liveJobLine" class="jobline" style="margin-top:5px">Loading…</div></div><div class="spacer"></div><span id="liveJobState" class="state warn">ACTIVE</span></div><div class="progress"><div id="liveJobBar" class="bar" style="width:0"></div></div><pre id="liveJobLog" class="log install-live-log">Connecting…</pre>`,`<button class="btn" ${uiAction('closeModal')}>Run in background</button>`,true);
  let stopped=false;const tick=async()=>{if(stopped)return;const p=await updateLiveJobDetail(id);if(p?.finished&&timer){clearInterval(timer);timer=null}};let timer=setInterval(tick,650);MODAL_CLEANUP=()=>{stopped=true;if(timer)clearInterval(timer)};await tick();
};

/* Password visibility for login, RCON and every game-config Password field. */
function enhancePasswordFields(root=document){
  root.querySelectorAll('input[type="password"]:not([data-eye-ready])').forEach(input=>{input.dataset.eyeReady='1';const wrap=document.createElement('div');wrap.className='password-wrap';input.parentNode.insertBefore(wrap,input);wrap.appendChild(input);const b=document.createElement('button');b.type='button';b.className='password-eye';b.textContent='◉';b.title='Show password';b.setAttribute('aria-label','Show password');b.addEventListener('click',()=>{const show=input.type==='password';input.type=show?'text':'password';b.title=show?'Hide password':'Show password';b.setAttribute('aria-label',b.title);b.textContent=show?'○':'◉'});wrap.appendChild(b)})
}
const baseOpenModal=openModal;openModal=function(...args){baseOpenModal(...args);enhancePasswordFields($('#modal'))};enhancePasswordFields(document);
new MutationObserver(()=>enhancePasswordFields(document)).observe(document.body,{childList:true,subtree:true});

/* Reconnect attached stdin consoles after lifecycle changes performed from the workspace. */
const baseServerAction=action;
action=async function(id,a){
  await baseServerAction(id,a);
  if(!['start','restart','stop'].includes(a))return;
  if(SERVER_WORKSPACE.serverId!==id||SERVER_WORKSPACE.tab!=='console')return;
  const d=SERVERS.find(x=>x.id===id);if(!d||profileFor(d)?.caps?.console!=='stdin')return;
  await renderServerWorkspaceTab(d,'console');
};

/* Keep the complete-delete behavior introduced earlier. */
window.deleteServer=function(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;const bareMetal=d.hostingMode!=='Vm',hasSaves=Array.isArray(d.saveDirs)&&d.saveDirs.length>0;
  const permanent=bareMetal?`<div class="section">Delete server completely</div><div class="callout">This permanently stops the server and deletes the entire server folder. This cannot be undone.</div><div class="kv compact"><div>Folder to delete</div><div class="mono">${esc(d.guestWorkingDir||'')}</div></div><div class="checkrow" style="margin-top:14px"><input id="deleteFinalBackup" type="checkbox" ${hasSaves?'checked':'disabled'}><label for="deleteFinalBackup">Create a final save backup before deletion${hasSaves?'':' (no save folders configured)'}</label></div><div class="field"><label>Type the exact server name to confirm</label><input id="deleteConfirm" autocomplete="off" placeholder="${esc(d.name)}" ${uiAction('syncPermanentDeleteButton',[id],{event:'input'})}><div class="help">Required: <b>${esc(d.name)}</b></div></div><button id="permanentDeleteBtn" class="btn danger" disabled ${uiAction('permanentDeleteServer',[id])}>Permanently delete server and files</button>`:`<div class="section">Delete server completely</div><div class="callout info">Complete file deletion is disabled for Hyper-V servers because the server folder is inside the guest VM.</div>`;
  openModal('Delete — '+d.name,`<div class="section">Remove from GameServerControl</div><div class="help">Removes only the server definition. All game files and saves remain untouched.</div><button class="btn" ${uiAction('removeServerOnly',[id])}>Remove only</button>${permanent}`,`<button class="btn" ${uiAction('closeModal')}>Cancel</button>`,true)
};
window.removeServerOnly=async function(id){const d=SERVERS.find(x=>x.id===id);if(!confirm(`Remove "${d?.name||id}" from GameServerControl?\n\nServer files will remain untouched.`))return;try{await api(`/api/servers/${encodeURIComponent(id)}`,{method:'DELETE'});setNotice('Server removed. Files were kept.','ok');closeModal();await loadServers()}catch(e){setNotice('Remove failed: '+e.message,'err')}};
window.syncPermanentDeleteButton=function(id){const d=SERVERS.find(x=>x.id===id),input=$('#deleteConfirm'),button=$('#permanentDeleteBtn');if(button)button.disabled=!d||!input||input.value!==d.name};
window.permanentDeleteServer=async function(id){const d=SERVERS.find(x=>x.id===id);if(!d)return;const confirmation=$('#deleteConfirm')?.value||'';if(confirmation!==d.name)return setNotice('Type the exact server name first.','err');const createFinalBackup=!!$('#deleteFinalBackup')?.checked,button=$('#permanentDeleteBtn');if(button){button.disabled=true;button.textContent='Deleting…'}try{const result=await api(`/api/servers/${encodeURIComponent(id)}/delete-completely`,{method:'POST',body:JSON.stringify({confirmation,createFinalBackup})});setNotice(result.message||'Server permanently deleted.','ok');closeModal();await loadServers()}catch(e){setNotice('Permanent deletion cancelled/failed: '+e.message,'err');if(button){button.textContent='Permanently delete server and files';syncPermanentDeleteButton(id)}}};
