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
    defaultInstallPath: p.defaultInstallPath || p.defaultWindowsInstallPath || '',
    exe: p.hostExecutableRelative || p.executableRelative || '',
    args: p.hostDefaultStartArgs || p.defaultStartArgs || [],
    saveRel: p.hostSaveDirs || p.saveDirs || [],
    logRel: p.hostLogPath ?? p.logPath ?? null,
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
  tab: 'overview',
  console: null,
  renderToken: 0
};
const SERVER_FILTER = { query: '', state: 'all' };
function bindServerDashboardFilters() {
  const search = document.getElementById('serverSearchInput');
  if (search) search.addEventListener('input', () => {
    SERVER_FILTER.query = search.value.trim().toLocaleLowerCase();
    renderServers();
  });
  document.querySelectorAll('[data-server-filter]').forEach(button => {
    button.addEventListener('click', () => {
      SERVER_FILTER.state = button.dataset.serverFilter || 'all';
      document.querySelectorAll('[data-server-filter]').forEach(item => {
        const selected = item.dataset.serverFilter === SERVER_FILTER.state;
        item.classList.toggle('active', selected);
        item.setAttribute('aria-pressed', String(selected));
      });
      renderServers();
    });
  });
}
bindServerDashboardFilters();

const SERVER_WORKSPACE_BASE_SHOW_VIEW = showView;


function serverWorkspaceParseHash() {
  const match = location.hash.match(/^#server\/([^/]+)(?:\/([^/]+))?$/);
  if (!match) {
    SERVER_WORKSPACE.serverId = null;
    SERVER_WORKSPACE.tab = 'overview';
    return;
  }
  try { SERVER_WORKSPACE.serverId = decodeURIComponent(match[1]); }
  catch { SERVER_WORKSPACE.serverId = match[1]; }
  SERVER_WORKSPACE.tab = match[2] || 'overview';
}

function serverWorkspaceCleanupConsole() {
  serverWorkspaceStopUpdatePolling();
  if (!SERVER_WORKSPACE.console) return;
  try { SERVER_WORKSPACE.console.close(); } catch { }
  SERVER_WORKSPACE.console = null;
}

let serverWorkspaceApprovedNavigation = false;
function openServerPage(id, tab = 'overview') {
  const nextHash = `#server/${encodeURIComponent(id)}/${tab}`;
  // Assigning an unchanged hash fires no hashchange event. Do not leave the
  // one-shot navigation approval armed for a later browser Back operation.
  if (location.hash === nextHash) return;
  if (SERVER_WORKSPACE.serverId && !serverWorkspaceConfirmLeave()) return;
  serverWorkspaceApprovedNavigation = true;
  SERVER_WORKSPACE_BASE_SHOW_VIEW('servers');
  location.hash = nextHash;
}
function serverWorkspaceHome() {
  if (SERVER_WORKSPACE.serverId && !serverWorkspaceConfirmLeave()) return;
  serverWorkspaceApprovedNavigation = true;
  serverWorkspaceCleanupConsole();
  location.hash = 'servers';
}
function serverWorkspaceSetTab(tab) {
  if (!SERVER_WORKSPACE.serverId) return;
  const def = SERVERS.find(x => x.id === SERVER_WORKSPACE.serverId);
  const resolved = def ? serverWorkspaceResolveTab(def, tab) : tab;
  const nextHash = `#server/${encodeURIComponent(SERVER_WORKSPACE.serverId)}/${resolved}`;
  if (location.hash === nextHash) return;
  if (!serverWorkspaceConfirmLeave()) return;
  serverWorkspaceApprovedNavigation = true;
  location.hash = nextHash;
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
  if (view !== 'servers' && SERVER_WORKSPACE.serverId) {
    if (!serverWorkspaceConfirmLeave()) return;
    serverWorkspaceCleanupConsole();
    SERVER_WORKSPACE.serverId = null;
    SERVER_WORKSPACE.tab = 'overview';
    history.replaceState(null, '', '#' + view);
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
  tabs.push(['backups', 'Backups'], ['network', 'Network']);
  if (def.steamAppId) tabs.push(['updates', 'Updates']);
  tabs.push(['schedule', 'Schedule'], ['settings', 'Settings']);
  return tabs;
}

// Keep existing leaf routes (#server/id/config, /schedule, etc.) stable.
// Group tasks by intent and use an explicit Updates page for manual SteamCMD
// work; schedules only create recurring jobs, never act immediately.
function serverWorkspaceSections(def) {
  const available = new Map(serverWorkspaceTabs(def));
  const rcon = !!profileFor(def)?.caps?.rcon || !!def.rconPort;
  if (rcon) available.set('access', 'Access & RCON');
  const groups = [
    ['overview', 'Overview', ['overview']],
    ['console', 'Console & players', ['console', 'players', 'admin']],
    ['configuration', 'Configuration', ['config', 'access', 'network', 'settings']],
    ['content', 'Files & mods', ['files', 'mods']],
    ['maintenance', 'Maintenance', ['updates', 'backups', 'schedule']],
    ['diagnostics', 'Diagnostics', ['diagnostics']]
  ];
  return groups.map(([key, label, leaves]) => ({
    key, label, tabs: leaves.filter(leaf => available.has(leaf)).map(leaf => [leaf,
      leaf === 'config' ? 'Game settings' :
      leaf === 'settings' ? 'Advanced & notifications' :
      leaf === 'updates' ? 'Updates' :
      leaf === 'schedule' ? 'Schedule' : available.get(leaf)])
  })).filter(section => section.tabs.length);
}

function serverWorkspaceSectionFor(def, tab) {
  const sections = serverWorkspaceSections(def);
  return sections.find(section => section.key === tab ||
    section.tabs.some(([leaf]) => leaf === tab)) || sections[0];
}

function serverWorkspaceResolveTab(def, tab) {
  const section = serverWorkspaceSectionFor(def, tab);
  if (section.tabs.some(([leaf]) => leaf === tab)) return tab;
  return section.tabs[0][0];
}

function serverWorkspaceValidTab(def, tab) {
  return serverWorkspaceResolveTab(def, tab);
}

function serverWorkspaceHasUnsavedChanges() {
  if (SERVER_WORKSPACE.tab === 'files' && window.GSC_FILES?.dirty) return true;
  const body = document.getElementById('swTabBody');
  if (!body) return false;
  return Array.from(body.querySelectorAll('[data-cfg], #swNetworkPorts, #swScheduleRestart, #swRestartHour, #swUpdate, #swDay, #swUpdateHour, #swBackup, #swBackupMinute, #swAccessHost, #swAccessPort, #swAccessPassword, #swAccessClear, #swDiscordUrl, #swDiscordAlertsOnly, #swBackupLabel, #swBackupRetention, #swAutoUpdateOnStart, #swAutoUpdateOnRestart')).some(input => {
    if (input.disabled) return false;
    if (input.type === 'checkbox') return input.checked !== input.defaultChecked;
    if (input.tagName === 'SELECT') {
      const options = Array.from(input.options);
      const defaults = options.filter(option => option.defaultSelected);
      // HTML single-select defaults to its first option when none declares
      // "selected". That is not an unsaved edit.
      if (!input.multiple && defaults.length === 0) return input.selectedIndex > 0;
      return options.some(option => option.selected !== option.defaultSelected);
    }
    return input.value !== input.defaultValue;
  });
}

function serverWorkspaceConfirmLeave() {
  if (!serverWorkspaceHasUnsavedChanges()) return true;
  if (!confirm('You have unsaved changes on this server page. Discard them?')) return false;
  // The file editor maintains its own draft state. Explicitly discard it after
  // user confirmation so returning to Files cannot silently restore old edits.
  if (SERVER_WORKSPACE.tab === 'files' && window.GSC_FILES?.dirty) {
    window.GSC_FILES.dirty = false;
    window.GSC_FILES.content = null;
  }
  return true;
}



function renderAllHostDashboard(root){
  const groups=Array.isArray(HOST_OVERVIEW)?HOST_OVERVIEW:[];
  const entries=groups.flatMap(host=>(host.servers||[]).map(server=>({host,server})));
  const running=entries.filter(x=>x.server.processState==='Running').length;
  const issues=entries.filter(x=>x.server.hasError).length+groups.filter(x=>!x.online).length;
  const metrics=[['Game servers',entries.length],['Running',running],
    ['Online computers',groups.filter(x=>x.online).length+'/'+groups.length],['Attention needed',issues]];
  const stats='<div class="dashboard-stats">'+metrics.map(([label,value])=>
    '<div class="dashboard-stat"><div><span data-gsc-i18n="'+esc(label)+'">'+esc(label)+'</span><strong>'+esc(value)+'</strong></div></div>'
  ).join('')+'</div>';
  const query=SERVER_FILTER.query, filter=SERVER_FILTER.state;
  const sections=groups.map(host=>{
    const filtered=(host.servers||[]).filter(server=>{
      if(filter==='running'&&server.processState!=='Running')return false;
      if(filter==='stopped'&&server.processState==='Running')return false;
      return !query||[host.name,server.id,server.name,server.gameType]
        .some(value=>String(value||'').toLocaleLowerCase().includes(query));
    });
    if(host.online&&!filtered.length&&(query||filter!=='all'))return '';
    const rows = filtered.map(server =>
      serverWorkspaceRenderListRow(server, server, {hostId: host.id, hostName: host.name})
    ).join('');
    return '<section class="host-group"><div class="host-group-head"><div>'+
      '<div class="host-group-name">'+esc(host.name)+(host.isLocal?' <span class="tiny muted" data-gsc-i18n="(this computer)">(this computer)</span>':'')+
      '</div><div class="host-group-caption">'+esc(host.id)+'</div></div>'+
      '<span class="host-group-state '+(host.online?'':'offline')+'">'+(host.online?'Online':'Offline')+'</span>'+
      '<span class="host-group-count">'+esc((host.servers||[]).length)+' servers</span></div>'+
      (host.online?(rows?'<div class="sw-server-list" role="list">'+rows+'</div>':
      '<div class="sw-empty" data-gsc-i18n="No servers registered on this computer.">No servers registered on this computer.</div>'):
      '<div class="host-group-offline" data-gsc-i18n="Computer unreachable. Check the Agent, network and managed tokens.">Computer unreachable. Check the Agent, network and managed tokens.</div>')+
      '</section>';
  }).filter(Boolean).join('');
  root.innerHTML=stats+'<div class="dashboard-section-head"><div><h2 data-gsc-i18n="All computers">All computers</h2>'+
    '<p data-gsc-i18n="Central overview of your registered Agent hosts.">Central overview of your registered Agent hosts.</p></div></div>'+
    (sections||'<div class="empty" data-gsc-i18n="No computers matched the current filter.">No computers matched the current filter.</div>');
}

function serverWorkspaceRenderListActions(id, name, state, steamAppId, hostId = null) {
  const up = state === 'Running';
  const busy = ['Starting', 'Stopping', 'Restarting', 'Working'].includes(state) ||
    (hostId === null && !!GSCUX.actions[id]);
  const readOnly = isReadOnly();
  const route = op => hostId === null
    ? uiAction('action', [id, op])
    : uiAction('serverWorkspaceHostAction', [hostId, id, op]);
  const button = (op, text, disabled, extraClass = '', reason = '') =>
    `<button type="button" class="btn sw-list-action ${extraClass}" ${disabled ? 'disabled' : ''}
       aria-label="${esc(text + ' ' + name)}" ${reason ? 'title="' + esc(reason) + '"' : ''}
       ${route(op)} data-gsc-i18n="${esc(text)}">${text}</button>`;
  const updateReason = !steamAppId ? 'No Steam App ID configured'
    : up ? 'Stop the server before updating game files' : 'Update and validate game files';
  return button('start', 'Start', up || busy || readOnly, 'primary') +
    button('stop', 'Stop', !up || busy || readOnly, 'danger') +
    button('restart', 'Restart', !up || busy || readOnly) +
    button('update', 'Update', up || busy || readOnly || !steamAppId, '', updateReason);
}

function serverWorkspaceRenderListRow(def, status, options = {}) {
  const remoteHostId = options.hostId ?? null;
  const isHostOverview = remoteHostId !== null;
  const meta = isHostOverview
    ? {icon: '▣', label: def.gameType || 'Game server'}
    : serverMeta(def);
  const actual = status.processState || 'Unknown';
  const pending = isHostOverview ? null : GSCUX.actions[def.id];
  const display = pending?.state || actual;
  const subtitle = esc(meta.label) + ' · ' +
    esc(isHostOverview ? (options.hostName || '') :
      def.hostingMode === 'Vm' ? 'Hyper-V' : 'This computer');
  const open = isHostOverview
    ? uiAction('selectHostAndOpen', [remoteHostId, def.id])
    : uiAction('openServerPage', [def.id, 'overview']);
  const num = value => value == null ? '—' : esc(value);
  const cpu = status.cpuPercent == null ? '—' : Number(status.cpuPercent).toFixed(1) + '%';
  const ram = status.memoryMB == null ? '—' : status.memoryMB + ' MB';
  const hasError = isHostOverview ? status.hasError : !!status.lastError;
  return `<div class="sw-server-list-row" role="listitem">
    <div class="sw-list-identity">
      <span class="sw-list-icon" aria-hidden="true">${esc(meta.icon)}</span>
      <div class="sw-list-name-wrap">
        <button class="sw-list-name" type="button" ${open}>${esc(def.name)}</button>
        <div class="sw-list-sub">${subtitle}${hasError
          ? ' · <span class="sw-list-error" data-gsc-i18n="Needs attention">Needs attention</span>' : ''}</div>
      </div>
    </div>
    <span class="sw-card-state ${serverWorkspaceStateClass(display)}" aria-label="Server status: ${esc(display)}"><i aria-hidden="true"></i><span data-gsc-i18n="${esc(display)}">${esc(display)}</span></span>
    <div class="sw-list-metrics">
      <span><small>CPU</small><b>${esc(cpu)}</b></span>
      <span><small>RAM</small><b>${esc(ram)}</b></span>
      <span><small>PID</small><b>${num(status.pidInGuest)}</b></span>
    </div>
    <div class="sw-list-actions" role="group" aria-label="Actions for ${esc(def.name)}">
      ${serverWorkspaceRenderListActions(def.id, def.name, actual, def.steamAppId, remoteHostId)}
      <button type="button" class="btn sw-list-action sw-list-manage" ${open}
        aria-label="Manage ${esc(def.name)}">Manage</button>
    </div>
  </div>`;
}

async function serverWorkspaceHostAction(hostId, serverId, operation) {
  if (isReadOnly() || !['start', 'stop', 'restart', 'update'].includes(operation)) return;
  if (ACTIVE_HOST_ID !== hostId) await selectHost(hostId);
  if (ACTIVE_HOST_ID !== hostId) return;
  if (!SERVERS.some(def => def.id === serverId))
    return toast('The selected server is no longer available on this computer.', 'err');
  await action(serverId, operation);
}
window.serverWorkspaceHostAction = serverWorkspaceHostAction;

renderServers = function () {
  const root = $('#servers');
  if (!root) return;
  const tools = $('#serverListTools');
  if (ACTIVE_HOST_ID==='all') {
    if (tools) tools.classList.remove('hidden');
    setWorkspaceHeading('All computers','CONTROL PANEL / MULTI-AGENT');
    serverWorkspaceCleanupConsole();
    renderAllHostDashboard(root);
    return;
  }

  if (SERVER_WORKSPACE.serverId) {
    const def = SERVERS.find(x => x.id === SERVER_WORKSPACE.serverId);
    if (def) {
      if (tools) tools.classList.add('hidden');
      renderServerWorkspace(def);
      return;
    }
    SERVER_WORKSPACE.serverId = null;
  }

  setWorkspaceHeading('Servers', 'CONTROL PANEL / OVERVIEW');
  if (tools) tools.classList.remove('hidden');
  serverWorkspaceCleanupConsole();

  const online = SERVERS.filter(def => serverWorkspaceStatus(def.id).processState === 'Running').length;
  const offline = SERVERS.length - online;
  const issues = SERVERS.filter(def => !!serverWorkspaceStatus(def.id).lastError).length;
  const stats = `<div class="dashboard-stats">
    <div class="dashboard-stat"><span class="dashboard-stat-icon">▦</span><div><span data-gsc-i18n="Total servers">Total servers</span><strong>${SERVERS.length}</strong></div></div>
    <div class="dashboard-stat stat-success"><span class="dashboard-stat-icon">●</span><div><span data-gsc-i18n="Running">Running</span><strong>${online}</strong></div></div>
    <div class="dashboard-stat"><span class="dashboard-stat-icon">◯</span><div><span data-gsc-i18n="Offline">Offline</span><strong>${offline}</strong></div></div>
    <div class="dashboard-stat ${issues ? 'stat-warning' : ''}"><span class="dashboard-stat-icon">!</span><div><span data-gsc-i18n="Reported errors">Reported errors</span><strong>${issues}</strong></div></div>
  </div>`;
  if (!SERVERS.length) {
    if (tools) tools.classList.add('hidden');
    root.innerHTML = stats + `<div class="empty"><div class="empty-symbol">▦</div><h2 data-gsc-i18n="No servers yet">No servers yet</h2><p data-gsc-i18n="Add an existing game server or install a new one to get started.">Add an existing game server or install a new one to get started.</p><div class="empty-actions"><button class="btn primary" ${uiAction('openCreate')} data-gsc-i18n="Create server">Create server</button> <button class="btn" ${uiAction('openDiscover')} data-gsc-i18n="Discover installed games">Discover installed games</button></div></div>`;
    return;
  }

  const filtered = SERVERS.filter(def => {
    const state = String(serverWorkspaceStatus(def.id).processState || 'Unknown');
    if (SERVER_FILTER.state === 'running' && state !== 'Running') return false;
    if (SERVER_FILTER.state === 'stopped' && state === 'Running') return false;
    if (!SERVER_FILTER.query) return true;
    return [def.name, def.id, def.gameType, def.steamAppId, def.guestWorkingDir]
      .some(field => String(field || '').toLocaleLowerCase().includes(SERVER_FILTER.query));
  }).sort((a, b) => {
    const aOnline = serverWorkspaceStatus(a.id).processState === 'Running' ? 0 : 1;
    const bOnline = serverWorkspaceStatus(b.id).processState === 'Running' ? 0 : 1;
    return aOnline - bOnline || a.name.localeCompare(b.name);
  });
  const rows = filtered.map(def =>
    serverWorkspaceRenderListRow(def, serverWorkspaceStatus(def.id))
  ).join('');
  root.innerHTML = stats + `<div class="dashboard-section-head"><div><h2 data-gsc-i18n="Your servers">Your servers</h2><p data-gsc-i18n="Monitor and manage your game instances.">Monitor and manage your game instances.</p></div><span class="dashboard-count">${filtered.length} of ${SERVERS.length}</span></div>` +
    (filtered.length ? `<div class="sw-server-list" role="list">${rows}</div>` :
      '<div class="empty filtered-empty"><h2 data-gsc-i18n="No matching servers">No matching servers</h2><p data-gsc-i18n="Try another search term or status filter.">Try another search term or status filter.</p></div>');
};

function updateServerWorkspaceHeader(def) {
  const status = serverWorkspaceStatus(def.id);
  const pending = window.GSCUX?.actions?.[def.id];
  const state = pending?.state || status.processState || 'Unknown';
  const running = status.processState === 'Running';
  const busy = !!pending || ['Starting', 'Stopping'].includes(status.processState);

  const stateEl = $('#swState');
  if (stateEl) {
    if(globalThis.GSC_I18N)globalThis.GSC_I18N.set(stateEl,state);
    else stateEl.textContent=state;
    stateEl.className = 'sw-state ' + serverWorkspaceStateClass(state);
  }
  const start = $('#swStart'), stop = $('#swStop'), restart = $('#swRestart'), update = $('#swUpdateButton');
  if (start) start.disabled = running || busy;
  if (stop) stop.disabled = !running || busy;
  if (restart) restart.disabled = !running || busy;
  if (update) update.disabled = running || busy || !def.steamAppId || isReadOnly();
  const manualUpdate = $('#swManualUpdateButton');
  if (manualUpdate) manualUpdate.disabled = running || busy || !def.steamAppId || isReadOnly();

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
  const backup = $('#swOverviewBackup'), stateText = $('#swOverviewState'), error = $('#swOverviewError');
  if (backup) backup.textContent = status.lastBackupAt ? new Date(status.lastBackupAt).toLocaleString() : 'No backups yet';
  if (stateText) {
    const label=status.processState || 'Unknown';
    if(globalThis.GSC_I18N)globalThis.GSC_I18N.set(stateText,label);
    else if(stateText.textContent!==label)stateText.textContent=label;
  }
  if (error) error.textContent = status.lastError || 'No active errors reported';
}

function renderServerWorkspace(def) {
  const root = $('#servers');
  $('#serverListTools')?.classList.add('hidden');
  const requestedTab = serverWorkspaceValidTab(def, SERVER_WORKSPACE.tab);
  const section = serverWorkspaceSectionFor(def, requestedTab);
  setWorkspaceHeading(def.name, 'SERVERS / ' + section.label.toUpperCase(), false);
  const existing = root.querySelector('.sw-workspace');
  if (existing && existing.dataset.serverId === def.id && existing.dataset.tab === requestedTab) {
    updateServerWorkspaceHeader(def);
    return;
  }

  serverWorkspaceCleanupConsole();
  SERVER_WORKSPACE.tab = requestedTab;
  const meta = serverMeta(def);
  const sections = serverWorkspaceSections(def);

  root.innerHTML = `<div class="sw-workspace" data-server-id="${esc(def.id)}" data-tab="${esc(SERVER_WORKSPACE.tab)}">
    <div class="sw-head">
      <button type="button" class="sw-back" ${uiAction('serverWorkspaceHome')} aria-label="Back to all servers" title="Back to servers" data-gsc-i18n-aria-label="Back to all servers" data-gsc-i18n-title="Back to servers">←</button>
      <div class="sw-icon" aria-hidden="true">${esc(meta.icon)}</div>
      <div class="sw-identity"><div class="sw-name">${esc(def.name)}</div><div class="sw-meta">${esc(meta.label)} · ${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'Local host'} · ${esc(def.id)}</div></div>
      <div class="sw-actions"><span id="swState" class="sw-state"></span><span class="sw-action-divider" aria-hidden="true"></span>
        <button id="swStart" type="button" class="btn primary sw-power" ${uiAction('action',[def.id,'start'])} data-gsc-i18n="Start">Start</button>
        <button id="swRestart" type="button" class="btn sw-power" ${uiAction('action',[def.id,'restart'])} data-gsc-i18n="Restart">Restart</button>
        <button id="swStop" type="button" class="btn danger sw-power" ${uiAction('action',[def.id,'stop'])} data-gsc-i18n="Stop">Stop</button>
        <button id="swUpdateButton" type="button" class="btn sw-power" title="Stop the server before updating game files" ${uiAction('action',[def.id,'update'])} data-gsc-i18n="Update" data-gsc-i18n-title="Stop the server before updating game files">Update</button>
      </div>
    </div>
    <nav class="sw-tabs" aria-label="Server sections" data-gsc-i18n-aria-label="Server sections">${sections.map(group => `<button type="button" class="sw-tab ${section.key === group.key ? 'active' : ''}" ${section.key === group.key ? 'aria-current="page"' : ''} ${uiAction('serverWorkspaceSetTab',[group.key])} data-gsc-i18n="${esc(group.label)}">${esc(group.label)}</button>`).join('')}</nav>
    ${section.tabs.length > 1 ? `<nav class="sw-subtabs" aria-label="${esc(section.label)} pages">${section.tabs.map(([key, label]) => `<button type="button" class="sw-subtab ${requestedTab === key ? 'active' : ''}" ${requestedTab === key ? 'aria-current="page"' : ''} ${uiAction('serverWorkspaceSetTab',[key])} data-gsc-i18n="${esc(label)}">${esc(label)}</button>`).join('')}</nav>` : ''}
    <div class="sw-body"><div id="swOperation" class="sw-operation"></div><div id="swTabBody"><div class="muted" data-gsc-i18n="Loading…">Loading…</div></div></div>
  </div>`;
  updateServerWorkspaceHeader(def);
  renderServerWorkspaceTab(def, SERVER_WORKSPACE.tab);
}

async function renderServerWorkspaceTab(def, tab) {
  const token = ++SERVER_WORKSPACE.renderToken;
  const body = $('#swTabBody');
  if (!body) return;
  body.innerHTML = '<div class="muted" data-gsc-i18n="Loading…">Loading…</div>';
  try {
    if (tab === 'console') await renderServerConsole(def, body);
    else if (tab === 'overview') renderServerOverview(def, body);
    else if (tab === 'diagnostics') await renderServerDiagnostics(def, body);
    else if (tab === 'config') await renderServerConfig(def, body);
    else if (tab === 'access') renderServerAccess(def, body);
    else if (tab === 'players') await renderServerPlayers(def, body);
    else if (tab === 'mods') renderServerMods(def, body);
    else if (tab === 'backups') await renderServerBackups(def, body);
    else if (tab === 'network') renderServerNetwork(def, body);
    else if (tab === 'updates') renderServerUpdates(def, body);
    else if (tab === 'schedule') await renderServerSchedule(def, body);
    else if (tab === 'settings') renderServerSettings(def, body);
    if (token !== SERVER_WORKSPACE.renderToken) return;
  } catch (e) {
    body.innerHTML = `<div class="callout">${esc(e.message)}</div>`;
  }
}

// The public join address comes exclusively from the configured VPS Relay.
// The Agent API URL is often localhost/Tailscale and is NOT a public game IP.
function serverWorkspaceConnectionDetails(relay) {
  const enabled=relay?.enabled===true;
  const configured=relay?.configured===true;
  const publicHost=enabled&&configured?String(relay.publicAddress||'').trim():'';
  const targetHost=enabled&&configured?String(relay.targetAddress||'').trim():'';
  const ports=enabled&&configured&&publicHost&&Array.isArray(relay.ports)
    ?relay.ports.filter(port=>{
      const start=Number(port.publicPort),end=port.publicEndPort==null?start:Number(port.publicEndPort);
      return Number.isInteger(start)&&Number.isInteger(end)&&start>=1&&end<=65535&&end>=start;
    }).map(port=>({
      name:String(port.name||'Game port'),
      protocol:String(port.protocol||'').toUpperCase(),
      start:Number(port.publicPort),
      end:port.publicEndPort==null?Number(port.publicPort):Number(port.publicEndPort)
    })):[];
  // Put the main gameplay port ahead of beacon/query/RCON entries.
  ports.sort((a,b)=>Number(!/^game$/i.test(a.name))-Number(!/^game$/i.test(b.name)));
  return {enabled,configured,publicHost,targetHost,ports,reachable:relay?.reachable===true};
}
function serverWorkspaceConnectionHost(host) {
  return host.includes(':')&&!host.startsWith('[')?'['+host+']':host;
}
function serverWorkspaceRenderConnectionDetails(relay) {
  const info=serverWorkspaceConnectionDetails(relay);
  if(!info.enabled)
    return '<div class="tiny muted" data-gsc-i18n="No public VPS Relay route enabled for this server. A public game IP cannot be inferred from the Agent URL.">No public VPS Relay route enabled for this server. A public game IP cannot be inferred from the Agent URL.</div>';
  if(!info.configured)
    return '<div class="tiny muted" data-gsc-i18n="VPS Relay is enabled, but not configured. Configure the Relay under Settings.">VPS Relay is enabled, but not configured. Configure the Relay under Settings.</div>';
  if(!info.publicHost)
    return '<div class="tiny muted" data-gsc-i18n="Public IP / hostname is not configured. Set the VPS Public address under Settings → Public Relay.">Public IP / hostname is not configured. Set the VPS Public address under Settings → Public Relay.</div>';
  const addresses=info.ports.map(port=>{
    const hostPort=serverWorkspaceConnectionHost(info.publicHost)+':'+port.start;
    const range=port.end===port.start?'':'–'+port.end;
    return `<div class="sw-public-port"><div><div class="tiny muted">${esc(port.name)} · ${esc(port.protocol)} ${esc(port.start+range)}</div><div class="sw-public-address mono">${esc(hostPort)}</div></div><button type="button" class="btn btn-small" ${uiAction('serverWorkspaceCopyPublicAddress',[hostPort])} data-gsc-i18n="Copy">Copy</button></div>`;
  }).join('');
  const note=info.reachable?'Relay API reachable; game port reachability is not verified.':
    'Relay API not reachable; public game connection is not verified.';
  return `<div class="sw-public-host"><span class="sw-public-address mono">${esc(info.publicHost)}</span><button type="button" class="btn btn-small" ${uiAction('serverWorkspaceCopyPublicAddress',[info.publicHost])} data-gsc-i18n="Copy IP">Copy IP</button></div>${addresses||'<div class="tiny muted" data-gsc-i18n="No public game ports configured for this Relay route.">No public game ports configured for this Relay route.</div>'}<div class="tiny muted">${esc(note)}</div>${info.targetHost?`<div class="tiny muted">Private/Tailscale target: ${esc(info.targetHost)} (not public)</div>`:''}`;
}
async function serverWorkspaceRefreshPublicAddress(id) {
  const target=$('#swPublicConnection');
  if(!target||SERVER_WORKSPACE.serverId!==id)return;
  target.textContent='Loading configured public address…';
  try{
    const relay=await api(`/api/servers/${encodeURIComponent(id)}/relay`);
    if(!target.isConnected||SERVER_WORKSPACE.serverId!==id)return;
    target.innerHTML=serverWorkspaceRenderConnectionDetails(relay);
  }catch(e){
    if(target.isConnected&&SERVER_WORKSPACE.serverId===id)
      target.textContent='Public address unavailable: '+e.message;
  }
}
async function serverWorkspaceCopyPublicAddress(address) {
  try{
    await navigator.clipboard.writeText(address);
    toast('Address copied.', 'ok');
  }catch(e){toast('Copy failed: '+e.message, 'err')}
}
window.serverWorkspaceRefreshPublicAddress=serverWorkspaceRefreshPublicAddress;
window.serverWorkspaceCopyPublicAddress=serverWorkspaceCopyPublicAddress;

function renderServerOverview(def, body) {
  const status = serverWorkspaceStatus(def.id);
  const ports = effectiveNetworkPorts(def);
  const metadata = Object.entries(status.metadata || {}).map(([key, value]) =>
    `<div>${esc(key)}</div><div>${esc(value)}</div>`).join('');
  const profile = profileFor(def);
  const sections = serverWorkspaceSections(def);
  const hasSection = key => sections.some(group => group.key === key);
  const quickActions = [
    ...(hasSection('console') ? [['Console & players', 'Live output and player management', 'console']] : []),
    ['Configuration', 'Game settings, RCON and network', 'configuration'],
    ...(def.steamAppId ? [['Updates', 'Run SteamCMD, view progress and configure auto-update', 'updates']] : []),
    ['Backups', 'Create, restore and manage backups', 'backups'],
    ['Schedule', 'Timed restarts, updates and backups', 'schedule'],
    ...(hasSection('content') ? [['Files & mods', 'Server files and installed content', 'content']] : []),
    ['Diagnostics', 'Errors and operation history', 'diagnostics']
  ];
  body.innerHTML = `<section class="sw-section sw-public-endpoint"><div class="sw-section-heading"><h3 data-gsc-i18n="Public server address">Public server address</h3><button type="button" class="btn btn-small" ${uiAction('serverWorkspaceRefreshPublicAddress',[def.id])} data-gsc-i18n="Refresh">Refresh</button></div><div id="swPublicConnection" data-gsc-i18n="Loading configured public address…">Loading configured public address…</div></section><div class="sw-metrics">
      <div class="sw-metric"><span data-gsc-i18n="Processor">Processor</span><b id="swCpu">${status.cpuPercent != null ? Number(status.cpuPercent).toFixed(1) + '%' : '—'}</b></div>
      <div class="sw-metric"><span data-gsc-i18n="Memory">Memory</span><b id="swRam">${status.memoryMB != null ? status.memoryMB + ' MB' : '—'}</b></div>
      <div class="sw-metric"><span data-gsc-i18n="Process ID">Process ID</span><b id="swPid">${status.pidInGuest ?? '—'}</b></div>
      <div class="sw-metric"><span data-gsc-i18n="Last backup">Last backup</span><b id="swOverviewBackup">${status.lastBackupAt ? new Date(status.lastBackupAt).toLocaleString() : 'No backups yet'}</b></div>
    </div>
    <div class="sw-overview-grid">
      <section class="sw-section"><div class="sw-section-heading"><h3 data-gsc-i18n="Server details">Server details</h3><span class="tiny muted" data-gsc-i18n="Configuration">Configuration</span></div>
        <div class="sw-info"><div data-gsc-i18n="Process status">Process status</div><div id="swOverviewState">${esc(status.processState || 'Unknown')}</div>
          <div data-gsc-i18n="Game">Game</div><div>${esc(profile?.label || def.gameType || 'Custom')}</div>
          <div data-gsc-i18n="Host">Host</div><div>${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This computer'}</div>
          <div data-gsc-i18n="Server ID">Server ID</div><div class="mono">${esc(def.id)}</div>
          <div data-gsc-i18n="Network ports">Network ports</div><div>${ports.length ? ports.map(formatNetworkPort).map(esc).join('<br>') : 'No ports configured'}</div>
          ${metadata}
        </div>
      </section>
      <div class="sw-overview-side">
        <section class="sw-section"><div class="sw-section-heading"><h3 data-gsc-i18n="Quick access">Quick access</h3></div>
          <div class="sw-quick-links">${quickActions.map(([title, detail, tab]) =>
            `<button type="button" class="sw-quick-link" ${uiAction('serverWorkspaceSetTab',[tab])}><span><b>${title}</b><small>${detail}</small></span><span aria-hidden="true">→</span></button>`
          ).join('')}</div>
        </section>
        <section class="sw-section"><div class="sw-section-heading"><h3 data-gsc-i18n="Health">Health</h3><button type="button" class="btn btn-small" ${uiAction('serverWorkspaceSetTab',['diagnostics'])} data-gsc-i18n="View history">View history</button></div>
          <div class="sw-health-summary"><span class="sw-health-led ${status.lastError ? 'error' : ''}" aria-hidden="true"></span><div id="swOverviewError">${esc(status.lastError || 'No active errors reported')}</div></div>
        </section>
      </div>
    </div>`;
  void serverWorkspaceRefreshPublicAddress(def.id);
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
  ).join('') || '<div class="sw-empty" data-gsc-i18n="No backup, update or restore operation observed in this Agent session.">No backup, update or restore operation observed in this Agent session.</div>';
  const events = (d.events || []).map(event =>
    `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(event.kind)} · ${esc(event.severity)}</div><div class="sw-row-meta">${esc(serverDiagnosticTime(event.at))} · ${esc(event.message || '')}</div></div></div>`
  ).join('') || '<div class="sw-empty" data-gsc-i18n="No events recorded in this Agent session.">No events recorded in this Agent session.</div>';
  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Server diagnostics">Server diagnostics</b><div class="tiny muted" data-gsc-i18n="Agent-observed events only. Recovery source may not be identifiable.">Agent-observed events only. Recovery source may not be identifiable.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['diagnostics'])} data-gsc-i18n="Refresh">Refresh</button></div>
    <div class="sw-info"><div data-gsc-i18n="Process">Process</div><div>${esc(status.processState || 'Unknown')}</div><div data-gsc-i18n="Last started">Last started</div><div>${esc(serverDiagnosticTime(status.startedAt))}</div><div data-gsc-i18n="Last unexpected stop">Last unexpected stop</div><div>${esc(serverDiagnosticTime(d.lastUnexpectedStopAt))}</div><div data-gsc-i18n="Last recovery observed">Last recovery observed</div><div>${esc(serverDiagnosticTime(d.lastRecoveryObservedAt))}</div><div data-gsc-i18n="Last manual restart">Last manual restart</div><div>${esc(serverDiagnosticTime(d.lastManualRestartAt))}</div><div data-gsc-i18n="Last backup">Last backup</div><div>${esc(serverDiagnosticTime(status.lastBackupAt))}</div><div data-gsc-i18n="Last error">Last error</div><div>${esc(status.lastError || '—')}</div></div>
    <div class="sw-section"><h3 data-gsc-i18n="Backup, update and restore">Backup, update and restore</h3>${operations}</div>
    <div class="sw-section"><h3 data-gsc-i18n="Recent events">Recent events</h3>${events}</div>
    <div class="tiny muted">${esc(result.note || '')} For persistent crash details, inspect the game and Agent logs.</div>`;
}

async function renderServerConfig(def, body) {
  const result = await api(`/api/servers/${encodeURIComponent(def.id)}/config`);
  if (!result.schema) {
    body.innerHTML = '<div class="sw-empty" data-gsc-i18n="No managed configuration is available for this game.">No managed configuration is available for this game.</div>';
    return;
  }
  const values = result.values || {};
  let fields = '';
  for (const section of result.schema.sections || []) {
    fields += `<div class="sw-section"><h3>${esc(section.title)}</h3><div class="formgrid">`;
    for (const field of section.fields || []) fields += configField(field, configValue(values, field));
    fields += '</div></div>';
  }
  const canApply = serverWorkspaceStatus(def.id).processState === 'Running' && !GSCUX.actions[def.id];
  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Game configuration">Game configuration</b><div class="muted tiny" data-gsc-i18n="Save changes first. To apply them on a running server, use Restart to apply (this disconnects players).">Save changes first. To apply them on a running server, use Restart to apply (this disconnects players).</div></div><div class="spacer"></div><button class="btn" ${canApply ? '' : 'disabled'} title="Restart the running server using the saved game configuration" ${uiAction('action',[def.id,'apply'])} data-gsc-i18n="Restart to apply">Restart to apply</button><button class="btn primary" ${uiAction('serverWorkspaceSaveConfig',[def.id])} data-gsc-i18n="Save configuration">Save configuration</button></div>${fields}`;
}

async function serverWorkspaceSaveConfig(id) {
  const values = {};
  $('#swTabBody')?.querySelectorAll('[data-cfg]').forEach(el => values[el.dataset.cfg] = el.type === 'checkbox' ? String(el.checked) : String(el.value));
  try {
    await api(`/api/servers/${encodeURIComponent(id)}/config`, { method: 'PUT', body: JSON.stringify(values) });
    toast('Configuration saved.', 'ok');
    serverWorkspaceRefreshTab('config');
  } catch (e) { toast('Save failed: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveConfig = serverWorkspaceSaveConfig;

async function renderServerPlayers(def, body) {
  const list = await api(`/api/servers/${encodeURIComponent(def.id)}/rcon/players`);
  body.innerHTML = `<div class="sw-toolbar"><div class="muted tiny" data-gsc-i18n="Players returned by the game RCON handler.">Players returned by the game RCON handler.</div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['players'])} data-gsc-i18n="Refresh">Refresh</button></div>${list?.length ? list.map(player => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(player.name)}</div><div class="sw-row-meta">${esc(player.steamId)} · ${esc(player.address || player.playerUid || '')}</div></div><button class="btn" ${uiAction('serverWorkspacePlayerAction',[def.id,'KickPlayer',player.steamId])} data-gsc-i18n="Kick">Kick</button><button class="btn danger" ${uiAction('serverWorkspacePlayerAction',[def.id,'BanPlayer',player.steamId])} data-gsc-i18n="Ban">Ban</button></div>`).join('') : '<div class="sw-empty" data-gsc-i18n="No players returned by RCON.">No players returned by RCON.</div>'}`;
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

// RCON credentials are configured in the server definition, separate from
// game-specific admin passwords. Blank input preserves the existing secret.
function renderServerAccess(def, body) {
  const supportsRcon = !!profileFor(def)?.caps?.rcon || !!def.rconPort;
  if (!supportsRcon) {
    body.innerHTML = '<div class="sw-empty" data-gsc-i18n="This game does not expose a supported RCON connection.">This game does not expose a supported RCON connection.</div>';
    return;
  }
  body.innerHTML = `<div class="sw-section"><div class="sw-section-heading"><h3 data-gsc-i18n="RCON connection">RCON connection</h3></div>
    <p class="tiny muted" data-gsc-i18n="Configure the admin connection used by the console and supported player actions. This is separate from the game join/admin passwords.">Configure the admin connection used by the console and supported player actions. This is separate from the game join/admin passwords.</p>
    <div class="formgrid"><div class="field"><label for="swAccessHost" data-gsc-i18n="Host">Host</label><input id="swAccessHost" autocomplete="off" value="${esc(def.rconHost || 'auto')}"><div class="help" data-gsc-i18n="auto uses localhost or the guest IP.">auto uses localhost or the guest IP.</div></div>
    <div class="field"><label for="swAccessPort" data-gsc-i18n="Port">Port</label><input id="swAccessPort" type="number" min="1" max="65535" value="${esc(def.rconPort ?? '')}"></div></div>
    <div class="field"><label for="swAccessPassword" data-gsc-i18n="RCON password">RCON password</label><input id="swAccessPassword" type="password" name="gsc-rcon-setting" autocomplete="new-password" data-1p-ignore="true" data-lpignore="true" data-bwignore="true" spellcheck="false" placeholder="Leave empty to keep the current password"><div class="help">Existing password: ${def.rconPassword ? 'configured' : 'not configured'}. Enter a new value only to replace it.</div></div>
    <div class="checkrow"><input id="swAccessClear" type="checkbox"><label for="swAccessClear" data-gsc-i18n="Remove existing RCON password">Remove existing RCON password</label></div>
    <button class="btn primary" ${uiAction('serverWorkspaceSaveAccess',[def.id])} data-gsc-i18n="Save RCON connection">Save RCON connection</button>
  </div>`;
}
async function serverWorkspaceSaveAccess(id) {
  const def = SERVERS.find(x => x.id === id);
  if (!def) return;
  const portRaw = $('#swAccessPort')?.value.trim() || '';
  const port = portRaw ? Number(portRaw) : null;
  if (port !== null && (!Number.isInteger(port) || port < 1 || port > 65535))
    return toast('RCON port must be between 1 and 65535.', 'err');
  const password = $('#swAccessPassword')?.value || '';
  const remove = !!$('#swAccessClear')?.checked;
  if (remove && password) return toast('Choose either a new password or remove the old one.', 'err');
  const updated = { ...def, rconHost: $('#swAccessHost')?.value.trim() || 'auto',
    rconPort: port, rconPassword: remove ? null : password || def.rconPassword || null };
  try {
    await api(`/api/servers/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(updated) });
    toast('RCON connection saved.', 'ok');
    await loadServers();
    const current = SERVERS.find(x => x.id === id);
    if (current && SERVER_WORKSPACE.tab === 'access')
      renderServerWorkspaceTab(current, 'access');
  } catch (e) { toast('RCON connection failed: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveAccess = serverWorkspaceSaveAccess;

function renderServerMods(def, body) {
  body.innerHTML = `<div class="sw-section"><h3 data-gsc-i18n="Mods">Mods</h3><div class="muted tiny" style="margin-bottom:14px" data-gsc-i18n="The existing game-specific mod manager opens from here. Unsupported games do not get this tab.">The existing game-specific mod manager opens from here. Unsupported games do not get this tab.</div><button class="btn primary" ${uiAction('openMods',[def.id])} data-gsc-i18n="Open mod manager">Open mod manager</button></div>`;
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
    return `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(title)}</div><div class="sw-row-meta mono">${esc(backup.checkpointName)}</div><div class="sw-row-meta">${esc(details)}</div>${skipped ? `<div class="tiny" style="color:var(--warn);margin-top:4px">Warning: backup.skippedFiles = ${skipped}; locked/unavailable files were skipped.</div>` : ''}</div><span class="state ${kindClass}">${esc(kindLabel)}</span><button class="btn" ${restoreDisabled || backup.restorable === false ? 'disabled' : ''} ${uiAction('serverWorkspaceRestore',[def.id,backup.checkpointName])} data-gsc-i18n="Restore">Restore</button><button class="btn danger" ${uiAction('serverWorkspaceDeleteBackup',[def.id,backup.checkpointName])} data-gsc-i18n="Delete">Delete</button></div>`;
  }).join('') : '<div class="sw-empty" data-gsc-i18n="No backups yet.">No backups yet.</div>';

  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Backups">Backups</b><div class="tiny muted" data-gsc-i18n="Archives/checkpoints stay on the server. Cloud access sends only control requests.">Archives/checkpoints stay on the server. Cloud access sends only control requests.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['backups'])} data-gsc-i18n="Refresh">Refresh</button></div><div class="sw-metrics"><div class="sw-metric"><span data-gsc-i18n="Normal backups">Normal backups</span><b>${normal.length}</b></div><div class="sw-metric"><span data-gsc-i18n="Safety copies">Safety copies</span><b>${safety.length}</b></div><div class="sw-metric"><span data-gsc-i18n="Known storage">Known storage</span><b>${serverWorkspaceBackupSize(knownBytes)}</b></div><div class="sw-metric"><span data-gsc-i18n="Newest">Newest</span><b>${last ? new Date(last.createdAt).toLocaleString() : '—'}</b></div></div><div class="sw-section"><h3 data-gsc-i18n="Create backup">Create backup</h3><div class="formgrid"><div class="field"><label data-gsc-i18n="Backup name (optional)">Backup name (optional)</label><input id="swBackupLabel" maxlength="80" placeholder="Before mod update"><div class="help" data-gsc-i18n="Human-readable label only. GameServerControl keeps the archive/checkpoint identifier machine-safe.">Human-readable label only. GameServerControl keeps the archive/checkpoint identifier machine-safe.</div></div><div class="field"><label data-gsc-i18n="Retention">Retention</label><div style="display:flex;gap:8px"><input id="swBackupRetention" type="number" min="1" max="200" value="${settings.retention ?? 10}"><button class="btn" ${uiAction('serverWorkspaceSaveBackupRetention',[def.id])} data-gsc-i18n="Save">Save</button></div><div class="help">Keep ${settings.retention ?? 10} normal backups.${settings.isOverride ? ' Per-server override.' : ` Agent default: ${settings.defaultRetention ?? 10}.`} Safety copies are retained separately.</div></div></div><div class="sw-toolbar" style="margin-top:10px;margin-bottom:0"><div class="tiny muted">${esc(restoreHint)}</div><div class="spacer"></div><button class="btn primary" ${uiAction('serverWorkspaceCreateBackup',[def.id])} data-gsc-i18n="Backup now">Backup now</button></div></div><div class="callout info"><b data-gsc-i18n="Safety copy:">Safety copy:</b> before every restore, GameServerControl must create a complete pre-restore backup/checkpoint. If that safety copy fails, restore is aborted before current saves are replaced.${restoreDisabled ? `<br><b data-gsc-i18n="Restore disabled:">Restore disabled:</b> ${esc(restoreHint)}` : ''}</div>${rows}`;
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
  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Network">Network</b><div class="tiny muted" data-gsc-i18n="Port reservations and Windows Firewall metadata.">Port reservations and Windows Firewall metadata.</div></div><div class="spacer"></div>${profile?.ports?.length ? `<button class="btn" ${uiAction('serverWorkspaceResetNetwork',[def.id])} data-gsc-i18n="Profile defaults">Profile defaults</button>` : ''}<button class="btn primary" ${uiAction('serverWorkspaceSaveNetwork',[def.id])} data-gsc-i18n="Save">Save</button></div><div class="field"><label data-gsc-i18n="Reserved/listening ports — one per line">Reserved/listening ports — one per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:220px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help" data-gsc-i18n="Example: UDP 2456-2457 Game or TCP 7777 Game/API.">Example: UDP 2456-2457 Game or TCP 7777 Game/API.</div></div>`;
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
    serverWorkspaceRefreshTab('network');
  } catch (e) { toast('Network settings rejected: ' + e.message, 'err'); }
}
window.serverWorkspaceResetNetwork = serverWorkspaceResetNetwork;
window.serverWorkspaceSaveNetwork = serverWorkspaceSaveNetwork;


function renderServerUpdates(def, body) {
  const status = serverWorkspaceStatus(def.id);
  const up = status.processState === 'Running';
  const busy = !!GSCUX.actions[def.id] || ['Starting','Stopping','Restarting','Working'].includes(status.processState);
  const admin = !isReadOnly();
  const supported = def.hostingMode !== 'Vm' && !!def.steamAppId;
  const disabled = up || busy || !def.steamAppId || !admin ? 'disabled' : '';
  body.innerHTML = `<div class="sw-page-intro">
    <h2 data-gsc-i18n="Game updates">Game updates</h2>
    <p data-gsc-i18n="Run a SteamCMD update, follow its console output and configure updates on Start or Restart. Scheduled updates are configured separately.">Run a SteamCMD update, follow its console output and configure updates on Start or Restart. Scheduled updates are configured separately.</p>
  </div>
  <section class="sw-section">
    <div class="sw-section-heading"><h3 data-gsc-i18n="Manual update">Manual update</h3>
      <button id="swManualUpdateButton" class="btn primary" type="button" ${disabled}
        title="Update and validate game files. The game must be stopped."
        ${uiAction('action',[def.id,'update'])}>Update game now</button>
    </div>
    <div class="tiny muted">${up ? 'Stop this server before starting the update.' :
      busy ? 'Another server operation is in progress.' :
      'A pre-update safety backup is required. The game must remain stopped while SteamCMD updates its files.'}</div>
  </section>
  <section class="sw-section">
    <div class="sw-section-heading"><h3 data-gsc-i18n="SteamCMD update console">SteamCMD update console</h3>
      <button class="btn btn-small" type="button" ${admin ? '' : 'disabled'} ${uiAction('serverWorkspacePollUpdateConsole',[def.id,true])} data-gsc-i18n="Refresh output">Refresh output</button>
    </div>
    <div class="tiny muted" data-gsc-i18n="Shows the latest update attempt in the current Agent session. The full console is available to Admin users only.">Shows the latest update attempt in the current Agent session. The full console is available to Admin users only.</div>
    ${admin ? `<div id="swUpdateState" class="sw-update-state" data-gsc-i18n="Loading update status…">Loading update status…</div>
      <pre id="swUpdateTerminal" class="sw-terminal sw-update-terminal" role="log" aria-live="off" data-gsc-i18n="Loading…">Loading…</pre>` :
      '<div class="sw-empty" data-gsc-i18n="Admin access is required to read SteamCMD console output.">Admin access is required to read SteamCMD console output.</div>'}
  </section>
  <section class="sw-section">
    <div class="sw-section-heading"><h3 data-gsc-i18n="Automatic updates on lifecycle actions">Automatic updates on lifecycle actions</h3></div>
    <div class="tiny muted" data-gsc-i18n="SteamCMD runs before Start or Restart only when the matching option is enabled. A failed backup or update prevents the server from starting.">SteamCMD runs before Start or Restart only when the matching option is enabled. A failed backup or update prevents the server from starting.</div>
    <div class="checkrow"><input id="swAutoUpdateOnStart" type="checkbox" ${def.autoUpdateOnStart ? 'checked' : ''} ${supported && admin ? '' : 'disabled'}><label for="swAutoUpdateOnStart" data-gsc-i18n="Auto-update on Start">Auto-update on Start</label></div>
    <div class="checkrow"><input id="swAutoUpdateOnRestart" type="checkbox" ${def.autoUpdateOnRestart ? 'checked' : ''} ${supported && admin ? '' : 'disabled'}><label for="swAutoUpdateOnRestart" data-gsc-i18n="Auto-update on Restart">Auto-update on Restart</label></div>
    <div class="sw-toolbar"><button class="btn primary" type="button" ${supported && admin ? '' : 'disabled'} ${uiAction('serverWorkspaceSaveAutoUpdate',[def.id])} data-gsc-i18n="Save auto-update preferences">Save auto-update preferences</button>
      <span class="spacer"></span><button class="btn" type="button" ${uiAction('serverWorkspaceSetTab',['schedule'])} data-gsc-i18n="Configure weekly schedule">Configure weekly schedule</button></div>
    <div class="tiny muted">${supported ?
      'Both options are disabled by default. These preferences are separate from scheduled weekly updates.' :
      'Auto-update on Start/Restart requires a BareMetal Steam game server with a Steam App ID.'}</div>
  </section>`;
  if (admin) serverWorkspaceStartUpdatePolling(def.id);
}


async function renderServerSchedule(def, body) {
  const schedule = (await api(`/api/servers/${encodeURIComponent(def.id)}/schedule`).catch(() => null)) || {};
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  const linux = def.hostingMode !== 'Vm' && !serverWorkspaceLooksWindows(def);
  const platformInfo = linux
    ? '<div class="callout info" data-gsc-i18n="Windows Task Scheduler integration is not available on Linux. Use systemd timers or cron with the local Agent API instead.">Windows Task Scheduler integration is not available on Linux. Use systemd timers or cron with the local Agent API instead.</div>'
    : '';
  const autostart = !linux && def.hostingMode !== 'Vm' && serverWorkspaceLooksWindows(def)
    ? `<button type="button" class="btn" ${uiAction('openAutostart',[def.id])} data-gsc-i18n="Windows autostart">Windows autostart</button>` : '';
  const disabled = linux ? 'disabled' : '';
  const updateLink = def.steamAppId
    ? `<button class="btn" type="button" ${uiAction('serverWorkspaceSetTab',['updates'])} data-gsc-i18n="Open Updates">Open Updates</button>` : '';
  body.innerHTML = `<div class="sw-page-intro">
    <h2 data-gsc-i18n="Scheduled maintenance">Scheduled maintenance</h2>
    <p data-gsc-i18n="Set up recurring restarts, game updates and backups. To update a game immediately, use the separate Updates page.">Set up recurring restarts, game updates and backups. To update a game immediately, use the separate Updates page.</p>
  </div>
  ${platformInfo}
  <div class="sw-toolbar"><div><b data-gsc-i18n="Recurring tasks">Recurring tasks</b><div class="tiny muted" data-gsc-i18n="Times use the Agent computer&#39;s local timezone.">Times use the Agent computer's local timezone.</div></div>
    <div class="spacer"></div>${updateLink}${autostart}
    <button class="btn danger" type="button" ${uiAction('serverWorkspaceClearSchedule',[def.id])} data-gsc-i18n="Clear schedule">Clear schedule</button>
    <button class="btn primary" type="button" ${disabled} ${uiAction('serverWorkspaceSaveSchedule',[def.id])} data-gsc-i18n="Save schedule">Save schedule</button>
  </div>
  <section class="sw-section"><h3 data-gsc-i18n="Daily restart">Daily restart</h3>
    <div class="checkrow"><input id="swScheduleRestart" type="checkbox" ${schedule.dailyRestartEnabled ? 'checked' : ''} ${disabled}><label for="swScheduleRestart" data-gsc-i18n="Restart this server every day">Restart this server every day</label></div>
    <div class="field"><label for="swRestartHour" data-gsc-i18n="Hour (0–23)">Hour (0–23)</label><input id="swRestartHour" type="number" min="0" max="23" value="${schedule.dailyRestartHour ?? 5}" ${disabled}></div>
  </section>
  <section class="sw-section"><h3 data-gsc-i18n="Weekly game update">Weekly game update</h3>
    <div class="tiny muted" data-gsc-i18n="Runs the scheduled update task, not a manual update. SteamCMD updates require the game process to be stopped and a successful pre-update backup.">Runs the scheduled update task, not a manual update. SteamCMD updates require the game process to be stopped and a successful pre-update backup.</div>
    <div class="checkrow"><input id="swUpdate" type="checkbox" ${schedule.weeklyUpdateEnabled ? 'checked' : ''} ${disabled}><label for="swUpdate" data-gsc-i18n="Enable weekly updates">Enable weekly updates</label></div>
    <div class="formgrid"><div class="field"><label for="swDay" data-gsc-i18n="Day">Day</label><select id="swDay" ${disabled}>${days.map(day => `<option ${day === (schedule.weeklyUpdateDay || 'Wednesday') ? 'selected' : ''}>${day}</option>`).join('')}</select></div>
    <div class="field"><label for="swUpdateHour" data-gsc-i18n="Hour (0–23)">Hour (0–23)</label><input id="swUpdateHour" type="number" min="0" max="23" value="${schedule.weeklyUpdateHour ?? 4}" ${disabled}></div></div>
  </section>
  <section class="sw-section"><h3 data-gsc-i18n="Hourly backup">Hourly backup</h3>
    <div class="checkrow"><input id="swBackup" type="checkbox" ${schedule.hourlyBackupEnabled ? 'checked' : ''} ${disabled}><label for="swBackup" data-gsc-i18n="Back up every hour">Back up every hour</label></div>
    <div class="field"><label for="swBackupMinute" data-gsc-i18n="Minute (0–59)">Minute (0–59)</label><input id="swBackupMinute" type="number" min="0" max="59" value="${schedule.hourlyBackupMinute ?? 0}" ${disabled}></div>
  </section>`;
}

async function serverWorkspaceSaveSchedule(id) {
  const body = {
    dailyRestartEnabled: $('#swScheduleRestart').checked,
    dailyRestartHour: Number($('#swRestartHour').value || 5),
    weeklyUpdateEnabled: $('#swUpdate').checked,
    weeklyUpdateDay: $('#swDay').value,
    weeklyUpdateHour: Number($('#swUpdateHour').value || 4),
    hourlyBackupEnabled: $('#swBackup').checked,
    hourlyBackupMinute: Number($('#swBackupMinute').value || 0)
  };
  try { await api(`/api/servers/${encodeURIComponent(id)}/schedule`, { method: 'POST', body: JSON.stringify(body) }); toast('Schedule saved.', 'ok'); serverWorkspaceRefreshTab('schedule'); }
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
  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Advanced server settings">Advanced server settings</b><div class="tiny muted" data-gsc-i18n="Use game settings, RCON and Network above for routine changes.">Use game settings, RCON and Network above for routine changes.</div></div><div class="spacer"></div><button class="btn" ${uiAction('openEdit',[def.id])} data-gsc-i18n="Edit server definition">Edit server definition</button></div><div class="sw-info"><div data-gsc-i18n="Profile">Profile</div><div>${esc(profile?.label || 'Custom')}</div><div data-gsc-i18n="Run on">Run on</div><div>${def.hostingMode === 'Vm' ? 'Hyper-V virtual machine' : 'This machine'}</div><div data-gsc-i18n="Server folder">Server folder</div><div class="mono">${esc(def.guestWorkingDir || '')}</div><div data-gsc-i18n="Executable">Executable</div><div class="mono">${esc(def.guestExePath || '')}</div><div>Steam App ID</div><div>${esc(def.steamAppId || '—')}</div><div data-gsc-i18n="Log file">Log file</div><div class="mono">${esc(def.logPathInGuest || '—')}</div></div><details class="advanced"><summary data-gsc-i18n="Danger zone">Danger zone</summary><div class="advanced-body"><p class="tiny muted" data-gsc-i18n="Removing a server affects its registration and may remove associated files, depending on the selected action. Back up your data first.">Removing a server affects its registration and may remove associated files, depending on the selected action. Back up your data first.</p><button class="btn danger" ${uiAction('deleteServer',[def.id])} data-gsc-i18n="Remove server…">Remove server…</button></div></details>
  <div class="sw-section"><h3 data-gsc-i18n="Discord notifications">Discord notifications</h3><div id="swDiscordStatus" class="tiny muted" data-gsc-i18n="Loading notification settings…">Loading notification settings…</div><div class="field"><label data-gsc-i18n="Discord webhook (HTTPS)">Discord webhook (HTTPS)</label><input id="swDiscordUrl" type="password" class="mono" autocomplete="new-password" placeholder="Paste a new URL to set or replace; blank keeps current"><div class="help" data-gsc-i18n="The webhook is optional. Its URL is never shown in this settings panel.">The webhook is optional. Its URL is never shown in this settings panel.</div></div><div class="checkrow"><input id="swDiscordAlertsOnly" type="checkbox"><label for="swDiscordAlertsOnly" data-gsc-i18n="Only failures and unexpected stops (no routine start/stop/success messages)">Only failures and unexpected stops (no routine start/stop/success messages)</label></div><div class="sw-toolbar"><button class="btn primary" ${uiAction('serverWorkspaceSaveNotifications',[def.id])} data-gsc-i18n="Save notifications">Save notifications</button><button class="btn" ${uiAction('serverWorkspaceSendTestNotification',[def.id])} data-gsc-i18n="Send test">Send test</button><button class="btn danger" ${uiAction('serverWorkspaceClearNotifications',[def.id])} data-gsc-i18n="Disable">Disable</button></div><div class="tiny muted" data-gsc-i18n="Discord webhooks only send alerts. Remote commands require a separate secured Discord bot.">Discord webhooks only send alerts. Remote commands require a separate secured Discord bot.</div></div>`;
  serverWorkspaceLoadNotifications(def.id);

}

async function serverWorkspaceSaveAutoUpdate(id) {
  try {
    const autoUpdateOnStart = !!$('#swAutoUpdateOnStart')?.checked;
    const autoUpdateOnRestart = !!$('#swAutoUpdateOnRestart')?.checked;
    await api(`/api/servers/${encodeURIComponent(id)}/auto-update`, {
      method: 'PUT', body: JSON.stringify({ autoUpdateOnStart, autoUpdateOnRestart })
    });
    toast('Automatic update preferences saved.', 'ok');
    await loadServers(true);
    // Once persisted, the new selection is the browser's default. Avoid
    // an erroneous "unsaved changes" warning when navigating away.
    for (const id of ['swAutoUpdateOnStart', 'swAutoUpdateOnRestart']) {
      const checkbox = $('#' + id);
      if (checkbox) checkbox.defaultChecked = checkbox.checked;
    }
  } catch(e) { toast('Could not save automatic updates: ' + e.message, 'err'); }
}
window.serverWorkspaceSaveAutoUpdate = serverWorkspaceSaveAutoUpdate;

async function serverWorkspaceLoadNotifications(id) {
  const status = $('#swDiscordStatus');
  if (!status) return;
  try {
    const data = await api(`/api/servers/${encodeURIComponent(id)}/notifications`);
    if (!$('#swDiscordStatus')) return;
    status.textContent = data.configured ? 'Discord webhook configured.' : 'Disabled — no webhook configured.';
    const only = $('#swDiscordAlertsOnly');
    if (only) { only.checked = !!data.alertsOnly; only.defaultChecked = only.checked; }
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

async function createServerStatusConnection(onMessage, onClosed) {
  const hubPath = ACTIVE_HOST_ID==='local'||ACTIVE_HOST_ID==='all' ? '/hubs/status'
    : '/api/hosts/'+encodeURIComponent(ACTIVE_HOST_ID)+'/hubs/status';
  const negotiate = await fetch(CFG.url + hubPath + '/negotiate?negotiateVersion=1', {
    method: 'POST',
    headers: { Authorization: 'Bearer ' + CFG.token }
  });
  if (!negotiate.ok) throw new Error('SignalR negotiate HTTP ' + negotiate.status);

  const negotiated = await negotiate.json();
  const url = new URL(CFG.url);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.pathname = url.pathname.replace(/\/$/, '') + hubPath;
  url.search = '';
  url.searchParams.set('id', negotiated.connectionToken);
  url.searchParams.set('access_token', CFG.token);

  const ws = new WebSocket(url.toString());
  const pending = new Map();
  let sequence = 0;
  let ping = null;
  let handshakeComplete = false;

  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      try { ws.close(); } catch { }
      reject(new Error('WebSocket connection timed out.'));
    }, 7000);

    ws.onerror = () => {
      if (!handshakeComplete) {
        clearTimeout(timer);
        reject(new Error('WebSocket connection failed.'));
      }
    };

    ws.onclose = () => {
      if (ping) { clearInterval(ping); ping=null; }
      for (const request of pending.values()) request.reject(new Error('Connection closed.'));
      pending.clear();
      if (!handshakeComplete) {
        clearTimeout(timer);
        reject(new Error('WebSocket connection closed before handshake.'));
      } else onClosed?.();
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
            handshakeComplete = true;
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
        if (ws.readyState !== WebSocket.OPEN) return reject(new Error('Status connection is closed.'));
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
    body.innerHTML = '<div class="sw-empty" data-gsc-i18n="No live console integration is available for this game.">No live console integration is available for this game.</div>';
    return;
  }

  const isRcon = mode === 'rcon';
  const isStdin = mode === 'stdin';
  const readOnly = mode === 'log';
  const rconReady = !!def.rconPort && !!def.rconPassword;

  body.innerHTML = `<div class="sw-console"><div class="sw-console-head"><span class="sw-console-dot"></span><span>${isRcon ? 'RCON console' : isStdin ? 'Interactive process console' : 'Live log'}</span><span class="spacer"></span><span>${readOnly ? 'read only' : isRcon ? esc((def.rconHost || 'auto') + ':' + (def.rconPort || '—')) : 'stdin / stdout'}</span></div><pre id="swTerminal" class="sw-terminal"></pre>${readOnly ? '' : `<div class="sw-consolebar"><input id="swConsoleInput" autocomplete="off" placeholder="Type a server command…" ${isRcon && !rconReady ? 'disabled' : ''} ${uiAction('serverWorkspaceSendConsole',[def.id],{event:'keydown',key:'Enter'})}><button id="swConsoleSend" class="btn primary" ${isRcon && !rconReady ? 'disabled' : ''} ${uiAction('serverWorkspaceSendConsole',[def.id])} data-gsc-i18n="Send">Send</button></div>`}<div id="swConsoleHelp" class="sw-help">${readOnly ? esc(def.logPathInGuest || 'Following configured server log') : isRcon ? (rconReady ? 'Commands are sent through the game RCON handler.' : 'Configure RCON port/password in Settings first.') : 'Waiting for attached console state…'}</div></div>`;

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
let serverWorkspaceLastHash = location.hash;
window.addEventListener('hashchange', () => {
  const nextHash = location.hash;
  if (!serverWorkspaceApprovedNavigation && SERVER_WORKSPACE.serverId &&
      !serverWorkspaceConfirmLeave()) {
    history.replaceState(null, '', serverWorkspaceLastHash || '#servers');
    return;
  }
  serverWorkspaceApprovedNavigation = false;
  serverWorkspaceLastHash = nextHash;
  serverWorkspaceCleanupConsole();
  serverWorkspaceParseHash();
  if (nextHash === '#system' || nextHash === '#settings' || nextHash === '#installs') {
    originalServerWorkspaceShowView(nextHash === '#settings' ? 'system' : nextHash.slice(1));
    return;
  }
  if (CURRENT_VIEW !== 'servers') SERVER_WORKSPACE_BASE_SHOW_VIEW('servers');
  renderServers();
});


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
    firewallInfo = '<div class="callout info"><b data-gsc-i18n="Firewall:">Firewall:</b> This server runs inside Hyper-V. The host Agent does not alter the guest firewall; allow the listed ports inside the VM and forward/NAT them to the VM when required.</div>';
  } else if (exeRule) {
    firewallInfo = `<div class="callout info"><b data-gsc-i18n="Windows Firewall:">Windows Firewall:</b> GameServerControl allows the complete server executable while the server is running and removes its managed rules when it stops. Current lifecycle state: <b>${running ? 'rule active / server running' : 'rule closed / server stopped'}</b>.<br><span class="mono">${esc(def.guestExePath)}</span></div>`;
  } else if (windows) {
    firewallInfo = '<div class="callout info"><b data-gsc-i18n="Windows Firewall:">Windows Firewall:</b> This server launches through a script/JAR rather than a direct .exe, so GameServerControl does not create a program rule automatically. Use the ports below for a manual rule.</div>';
  } else {
    const commands = serverWorkspaceLinuxFirewallCommands(ports);
    firewallInfo = `<div class="callout info"><b data-gsc-i18n="Linux firewall:">Linux firewall:</b> the Agent intentionally runs unprivileged, so it does not modify UFW/firewalld. Open only the game ports you need on the host/router.${commands ? `<pre class="mono" style="white-space:pre-wrap;margin:10px 0 0">${esc(commands)}</pre>` : ''}</div>`;
  }

  const rconInfo = profile?.caps?.rcon && def.rconPort
    ? `<div class="callout info"><b>RCON:</b> ${esc(def.rconHost || 'auto')}:${esc(def.rconPort)} is a separate administration endpoint. If you expose it beyond localhost/private LAN, add the matching firewall/NAT rule deliberately and protect it with a strong password.</div>`
    : '';
  const portRows = ports.length
    ? ports.map(port => `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(port.name || 'Game port')}</div><div class="sw-row-meta mono">${esc(formatNetworkPort(port))}</div></div></div>`).join('')
    : '<div class="sw-empty" data-gsc-i18n="This profile has no curated network ports yet.">This profile has no curated network ports yet.</div>';

  body.innerHTML = `<div class="sw-toolbar"><div><b data-gsc-i18n="Network">Network</b><div class="tiny muted" data-gsc-i18n="Game profile ports, collision reservations and firewall behavior.">Game profile ports, collision reservations and firewall behavior.</div></div></div><div class="sw-metrics"><div class="sw-metric"><span data-gsc-i18n="Profile">Profile</span><b>${esc(profile?.label || 'Custom')}</b></div><div class="sw-metric"><span data-gsc-i18n="Reservations">Reservations</span><b>${ports.length}</b></div><div class="sw-metric"><span data-gsc-i18n="Game ports">Game ports</span><b>${portCount}</b></div><div class="sw-metric"><span data-gsc-i18n="Profile defaults">Profile defaults</span><b>${profileCount}</b></div></div>${firewallInfo}${rconInfo}<div class="sw-section"><h3 data-gsc-i18n="Effective game ports">Effective game ports</h3><div class="tiny muted" style="margin-bottom:10px" data-gsc-i18n="These reservations prevent conflicting BareMetal servers on the same Agent and tell you what to allow/forward. Hyper-V guests have independent network stacks.">These reservations prevent conflicting BareMetal servers on the same Agent and tell you what to allow/forward. Hyper-V guests have independent network stacks.</div>${portRows}</div><details class="advanced"><summary data-gsc-i18n="Advanced port override">Advanced port override</summary><div class="advanced-body"><div class="field"><label data-gsc-i18n="One port or range per line">One port or range per line</label><textarea id="swNetworkPorts" class="mono" style="min-height:180px">${esc(ports.map(formatNetworkPort).join('\n'))}</textarea><div class="help" data-gsc-i18n="Format: UDP 2456-2457 Game or TCP 7777 Game/API. Keep this aligned with the actual game configuration.">Format: UDP 2456-2457 Game or TCP 7777 Game/API. Keep this aligned with the actual game configuration.</div></div><div class="sw-toolbar">${profilePorts.length ? `<button class="btn" ${uiAction('serverWorkspaceResetNetwork',[def.id])}>Reset to ${esc(profile.label)} defaults</button>` : ''}<div class="spacer"></div><button class="btn primary" ${uiAction('serverWorkspaceSaveNetwork',[def.id])} data-gsc-i18n="Save override">Save override</button></div></div></details>`;
  body.insertAdjacentHTML('beforeend', `<div class="sw-section"><div class="sw-toolbar"><div><h3 data-gsc-i18n="Network diagnostics">Network diagnostics</h3><div class="tiny muted" data-gsc-i18n="Passive local socket check and configured Relay API health. No public game-port scan.">Passive local socket check and configured Relay API health. No public game-port scan.</div></div><div class="spacer"></div><button class="btn" type="button" ${uiAction('serverWorkspaceRunNetworkDiagnostics',[def.id])} data-gsc-i18n="Run diagnostics">Run diagnostics</button></div><div id="swNetworkDiagnosticsResult" class="tiny muted" data-gsc-i18n="Run a check to inspect local listening ports and Relay connectivity.">Run a check to inspect local listening ports and Relay connectivity.</div></div>`);
};

async function serverWorkspaceRunNetworkDiagnostics(id) {
  const root = $('#swNetworkDiagnosticsResult');
  if (!root) return;
  root.textContent = 'Checking configured ports and Relay API…';
  try {
    const result = await api(`/api/servers/${encodeURIComponent(id)}/network/diagnostics`);
    if (!root.isConnected) return;
    const sockets = result.sockets || {};
    const label = {
      ListeningAny: 'Listening (all interfaces)',
      InterfaceBound: 'Listening (specific interface)',
      LoopbackOnly: 'Loopback only',
      NotListening: 'No local listener',
      RangeTooLarge: 'Range too large to inspect'
    };
    const rows = (sockets.ports || []).map(port =>
      `<div class="sw-row"><div class="grow"><div class="sw-row-title">${esc(String(port.protocol || '').toUpperCase())} ${esc(port.port)} · ${esc(port.name || 'Game')}</div><div class="sw-row-meta">${esc(label[port.state] || port.state)}</div></div></div>`
    ).join('') || '<div class="muted" data-gsc-i18n="No configured ports to inspect.">No configured ports to inspect.</div>';
    const relay = result.relay || {};
    const relayText = !relay.enabled ? 'Not enabled for this server'
      : !relay.configured ? 'Not configured'
      : relay.reachable ? 'Relay API reachable' : 'Relay API unreachable';
    root.innerHTML = `<div class="sw-info"><div data-gsc-i18n="Server process">Server process</div><div>${esc(result.processState || 'Unknown')}</div><div>VPS Relay</div><div>${esc(relayText)}</div></div><div class="tiny muted" style="margin:10px 0">${esc(sockets.note || '')}</div>${rows}<div class="tiny muted" style="margin-top:9px">${esc(result.note || '')}</div>`;
  } catch (error) {
    if (root.isConnected) root.textContent = 'Network diagnostics failed: ' + error.message;
  }
}
window.serverWorkspaceRunNetworkDiagnostics = serverWorkspaceRunNetworkDiagnostics;


const SERVER_WORKSPACE_BASE_OPEN_CREATE_SETTINGS = openCreateSettings;
openCreateSettings = function (key) {
  SERVER_WORKSPACE_BASE_OPEN_CREATE_SETTINGS(key);
  const game = GAMES.find(x => x.key === key);
  const summary = document.querySelector('#modalBody .profile-summary');
  if (!game || !summary) return;
  const ports = game.ports || [];
  const count = serverWorkspacePortCount(ports);
  const conflict = findNetworkConflict(ports);
  summary.insertAdjacentHTML('beforeend', `<div class="section" data-gsc-i18n="Network">Network</div><div class="kv compact"><div data-gsc-i18n="Port reservations">Port reservations</div><div>${ports.length} (${count} actual port${count === 1 ? '' : 's'})</div><div data-gsc-i18n="Game ports">Game ports</div><div class="mono">${ports.length ? ports.map(formatNetworkPort).map(esc).join('<br>') : 'No curated defaults yet'}</div><div data-gsc-i18n="Windows firewall">Windows firewall</div><div>${/\.exe$/i.test(game.exe || '') ? 'Whole server EXE is allowed only while the server is running.' : 'Manual rule may be required for script/JAR launchers.'}</div><div data-gsc-i18n="Linux firewall">Linux firewall</div><div data-gsc-i18n="Ports are shown here; UFW/firewalld stays administrator-managed.">Ports are shown here; UFW/firewalld stays administrator-managed.</div></div>${conflict ? `<div class="callout"><b data-gsc-i18n="Port conflict:">Port conflict:</b> ${esc(formatNetworkPort(conflict.ours))} overlaps ${esc(conflict.server.name)} (${esc(formatNetworkPort(conflict.theirs))}). Change that server before installing/running both.</div>` : ''}`);
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
    body.innerHTML='<div class="muted" data-gsc-i18n="Loading config files…">Loading config files…</div>';
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
    if(!files.length){host.innerHTML='<div class="fse-empty" data-gsc-i18n="No matching config files.">No matching config files.</div>';return}
    const groups=new Map();
    for(const file of files){const label=groupLabel(file);if(!groups.has(label))groups.set(label,[]);groups.get(label).push(file)}
    host.innerHTML=[...groups.entries()].sort((a,b)=>a[0].localeCompare(b[0])).map(([label,items])=>`<div class="fse-group">${esc(label)}</div>${items.sort((a,b)=>String(a.path).localeCompare(String(b.path))).map(file=>`<button class="fse-file ${state.selected===file.path?'active':''}" ${uiAction('gscFilesOpen',[state.serverId,file.path])}><div class="fse-file-name">${esc(file.name)}</div><div class="fse-file-meta">${esc(file.path)}</div><div class="fse-file-badges"><span class="fse-badge">${esc(String(file.format||'text').toUpperCase())}</span><span class="fse-badge">${esc(sizeText(file.sizeBytes))}</span>${file.restartRecommended?'<span class="fse-badge warn" data-gsc-i18n="restart">restart</span>':''}</div></button>`).join('')}`).join('');
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
    // Keep the complete text visible, even when syntax tokenization would be expensive.
    const text=String(content??'');
    if(text.length>140000)return esc(text);
    return text.replace(/\r\n/g,'\n').replace(/\r/g,'\n').split('\n').map(line=>tokenizedLine(line,String(format||'').toLowerCase())).join('\n');
  }

  function currentInfo(){return state.list.find(x=>x.path===state.selected)||null}
  function setValidation(kind,text){const el=$('#fseValidation');if(!el)return;el.className='fse-status '+(kind||'');el.textContent=text}

  function syncEditorScroll(){
    const input=$('#fseText'),highlighted=$('#fseHighlight');
    if(!input||!highlighted)return;
    highlighted.scrollTop=input.scrollTop;
    highlighted.scrollLeft=input.scrollLeft;
  }

  function updateEditorPosition(){
    const input=$('#fseText'),position=$('#fsePosition');
    if(!input||!position)return;
    const before=input.value.slice(0,input.selectionStart),lastNewline=before.lastIndexOf('\n');
    const line=before.split('\n').length;
    position.textContent=`Ln ${line}, Col ${before.length-lastNewline}`;
  }

  function updateHighlight(){
    const input=$('#fseText'),highlighted=$('#fseHighlight');
    if(!input||!highlighted)return;
    // All file text is escaped by highlight/tokenizedLine before entering innerHTML.
    highlighted.innerHTML=highlight(input.value,state.content?.format||currentInfo()?.format)+'&#8203;';
    syncEditorScroll();
    updateEditorPosition();
  }

  function bindFileEditor(){
    const input=$('#fseText');
    if(!input)return;
    input.addEventListener('scroll',syncEditorScroll,{passive:true});
    input.addEventListener('click',updateEditorPosition);
    input.addEventListener('keyup',updateEditorPosition);
    input.addEventListener('select',updateEditorPosition);
    syncEditorScroll();
    updateEditorPosition();
  }

    function editorHtml(){
    const info=currentInfo(),c=state.content;
    if(!c||!info)return '<div class="fse-empty" data-gsc-i18n="Select a config file.">Select a config file.</div>';
    const running=serverWorkspaceStatus(state.serverId).processState==='Running';
    const restart=info.restartRecommended?`<span class="fse-status warn">Restart recommended${running?' · server running':''}</span>`:'<span class="fse-status ok" data-gsc-i18n="Reload usually not required">Reload usually not required</span>';
    const history=state.history.length?state.history.map(x=>`<div class="fse-history-row"><div class="grow"><div class="fse-file-name">${new Date(x.createdAt).toLocaleString()}</div><div class="fse-file-meta">${esc(sizeText(x.sizeBytes))} · ${esc(x.id)}</div></div><button class="btn" ${uiAction('gscFilesRestore',[x.id])} data-gsc-i18n="Restore">Restore</button></div>`).join(''):'<div class="muted tiny" data-gsc-i18n="No edit history yet. A snapshot is created automatically before every save or restore.">No edit history yet. A snapshot is created automatically before every save or restore.</div>';
    return `<div class="fse-head"><div class="grow"><div class="fse-title">${esc(info.name)}</div><div class="fse-path">${esc(info.path)}</div></div><div class="fse-head-actions"><button class="btn" ${uiAction('gscFilesReload')} data-gsc-i18n="Reload">Reload</button><button id="fseSave" class="btn primary" ${state.dirty?'':'disabled'} ${uiAction('gscFilesSave')} data-gsc-i18n="Save">Save</button></div></div><div class="fse-statusbar"><span class="fse-status">${esc(String(c.format||info.format||'text').toUpperCase())}</span><span class="fse-status">${esc(sizeText(c.sizeBytes))}</span><span class="fse-status">${new Date(c.modifiedAt).toLocaleString()}</span>${restart}<span id="fseValidation" class="fse-status" data-gsc-i18n="Unchanged">Unchanged</span></div>${info.restartRecommended&&running?'<div class="callout info" data-gsc-i18n="This file is normally read when the server/plugin starts. Save is allowed while running, but a restart is recommended for the change to take effect.">This file is normally read when the server/plugin starts. Save is allowed while running, but a restart is recommended for the change to take effect.</div>':''}<div class="fse-pane-title" data-gsc-i18n="Syntax-highlighted editor · Ctrl+S to save">Syntax-highlighted editor · Ctrl+S to save</div><div class="fse-code-surface"><pre id="fseHighlight" class="fse-highlight" aria-hidden="true">${highlight(c.content,c.format||info.format)}&#8203;</pre><textarea id="fseText" class="fse-editor" wrap="off" spellcheck="false" autocomplete="off" autocorrect="off" autocapitalize="off" aria-label="Edit configuration file" ${uiAction('gscFilesInput',[],{event:'input'})} ${uiAction('gscFilesKeydown',[],{event:'keydown',pass:'event'})}>${esc(c.content)}</textarea></div><div class="fse-editor-footer"><span id="fsePosition" data-gsc-i18n="Ln 1, Col 1">Ln 1, Col 1</span><span>${String(c.content||'').length>140000?'Large file · syntax colors disabled for performance':'Tab: 2 spaces · Enter: auto-indent'}</span></div><div class="fse-history"><div class="sw-toolbar"><div><b data-gsc-i18n="Edit history">Edit history</b><div class="tiny muted">Last ${state.history.length} snapshots for this file.</div></div><div class="spacer"></div></div>${history}</div>`;
  }

  function renderEditor(){
    const host=$('#fseMain');
    if(!host)return;
    host.innerHTML=editorHtml();
    bindFileEditor();
  }

  window.gscFilesFilter=function(value){state.filter=String(value||'');renderSidebar()};

  window.gscFilesOpen=async function(serverId,path,force=false){
    if(state.dirty&&!force&&state.selected&&state.selected!==path&&!confirm('Discard unsaved changes in the current config file?'))return;
    state.serverId=serverId;state.selected=path;state.dirty=false;renderSidebar();
    const host=$('#fseMain');if(host)host.innerHTML='<div class="fse-empty" data-gsc-i18n="Loading file…">Loading file…</div>';
    try{
      const encoded=encodeURIComponent(path);const [content,history]=await Promise.all([api(`/api/servers/${encodeURIComponent(serverId)}/files/content?path=${encoded}`),api(`/api/servers/${encodeURIComponent(serverId)}/files/history?path=${encoded}`)]);
      if(state.selected!==path)return;state.content=content;state.history=history||[];renderEditor();
    }catch(e){if(host)host.innerHTML=`<div class="callout">${esc(e.message)}</div>`}
  };

  window.gscFilesInput=function(){
    if(!state.content)return;state.dirty=true;const save=$('#fseSave');if(save)save.disabled=false;updateHighlight();setValidation('','Checking syntax…');
    clearTimeout(state.validationTimer);const seq=++state.validationSeq;
    state.validationTimer=setTimeout(async()=>{const input=$('#fseText');if(!input||seq!==state.validationSeq)return;try{await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/validate`,{method:'POST',body:JSON.stringify({path:state.selected,content:input.value})});if(seq===state.validationSeq)setValidation('ok','Syntax valid')}catch(e){if(seq===state.validationSeq)setValidation('err',e.message)}},650);
  };

  window.gscFilesKeydown=function(event){
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='s'){
      event.preventDefault();gscFilesSave();return;
    }
    const input=$('#fseText');
    if(!input||event.isComposing||event.ctrlKey||event.metaKey||event.altKey)return;
    if(event.key==='Tab'){
      event.preventDefault();
      const start=input.selectionStart,end=input.selectionEnd;
      if(!event.shiftKey&&start===end){
        input.setRangeText('  ',start,end,'end');
      }else{
        const lineStart=input.value.lastIndexOf('\n',start-1)+1;
        if(event.shiftKey&&start===end){
          const indent=input.value.slice(lineStart).match(/^(?:\t| {1,2})/);
          if(!indent)return;
          input.setRangeText('',lineStart,lineStart+indent[0].length,'start');
          const caret=Math.max(lineStart,start-indent[0].length);
          input.setSelectionRange(caret,caret);
        }else{
          const selectedEnd=end>start&&input.value[end-1]==='\n'?end-1:end;
          const newline=input.value.indexOf('\n',selectedEnd);
          const blockEnd=newline<0?input.value.length:newline;
          const block=input.value.slice(lineStart,blockEnd);
          const replacement=block.split('\n').map(line=>event.shiftKey?line.replace(/^(?:\t| {1,2})/,''):'  '+line).join('\n');
          input.setRangeText(replacement,lineStart,blockEnd,'start');
          input.setSelectionRange(lineStart,lineStart+replacement.length);
        }
      }
      gscFilesInput();
    }else if(event.key==='Enter'&&!event.shiftKey){
      event.preventDefault();
      const start=input.selectionStart,end=input.selectionEnd;
      const previousLine=input.value.slice(input.value.lastIndexOf('\n',start-1)+1,start);
      const indent=(previousLine.match(/^[ \t]*/)||[''])[0]+(/[\{\[\(]\s*$/.test(previousLine)?'  ':'');
      input.setRangeText('\n'+indent,start,end,'end');
      gscFilesInput();
    }
  };

  async function refreshList(){
    const list=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files`);state.list=list.files||[];renderSidebar();
  }
  async function refreshHistory(){state.history=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/history?path=${encodeURIComponent(state.selected)}`)||[];renderEditor()}

  window.gscFilesSave=async function(){
    const input=$('#fseText');if(!input||!state.content||!state.selected||!state.dirty)return;
    const save=$('#fseSave');if(save){save.disabled=true;save.textContent='Saving…'}
    input.readOnly=true;
    try{
      const payload={path:state.selected,content:input.value};
      await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/validate`,{method:'POST',body:JSON.stringify(payload)});
      const result=await api(`/api/servers/${encodeURIComponent(state.serverId)}/files/content`,{method:'PUT',body:JSON.stringify({...payload,expectedSha256:state.content.sha256})});
      state.content={...state.content,content:input.value,sha256:result.sha256,sizeBytes:result.sizeBytes,modifiedAt:result.modifiedAt};state.dirty=false;
      await refreshList();await refreshHistory();setValidation('ok','Saved · syntax valid');const info=currentInfo();setNotice(info?.restartRecommended?'Config saved. Restart recommended for this change.':'Config saved.','ok',false);
    }catch(e){setValidation('err',e.message);setNotice('Config save failed: '+e.message,'err',false);if(save)save.disabled=false}
    finally{input.readOnly=false;if(save)save.textContent='Save'}
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
    if(!result.supported){body.innerHTML=`<div class="callout info"><b data-gsc-i18n="Files editor unavailable.">Files editor unavailable.</b><br>${esc(result.reason||'This server does not support safe config-file access.')}</div>`;return}
    state.list=result.files||[];
    if(state.selected&&!state.list.some(x=>x.path===state.selected)){state.selected=null;state.content=null;state.history=[];state.dirty=false}
    if(!state.selected&&state.list.length)state.selected=state.list[0].path;
    body.innerHTML=`<div class="sw-toolbar"><div><b data-gsc-i18n="Config files">Config files</b><div class="tiny muted" data-gsc-i18n="Only existing text configs inside allowlisted server/mod roots are exposed. Arbitrary filesystem access is not available.">Only existing text configs inside allowlisted server/mod roots are exposed. Arbitrary filesystem access is not available.</div></div><div class="spacer"></div><button class="btn" ${uiAction('serverWorkspaceRefreshTab',['files'])} data-gsc-i18n="Rescan">Rescan</button></div><div class="fse-layout"><aside class="fse-sidebar"><div class="fse-filter"><input placeholder="Filter files, paths or plugins…" value="${esc(state.filter)}" ${uiAction('gscFilesFilter',[],{event:'input',pass:'value'})}></div><div id="fseList" class="fse-list"></div></aside><main id="fseMain" class="fse-main"></main></div>`;
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
  openModal(r.name||id,`<div class="install-live-head"><div><div class="jobphase">Job ${esc(id)} · <span id="liveJobPhase" data-gsc-i18n="Loading">Loading</span></div><div id="liveJobLine" class="jobline" style="margin-top:5px" data-gsc-i18n="Loading…">Loading…</div></div><div class="spacer"></div><span id="liveJobState" class="state warn" data-gsc-i18n="ACTIVE">ACTIVE</span></div><div class="progress"><div id="liveJobBar" class="bar" style="width:0"></div></div><pre id="liveJobLog" class="log install-live-log" data-gsc-i18n="Connecting…">Connecting…</pre>`,`<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Run in background">Run in background</button>`,true);
  let stopped=false;const tick=async()=>{if(stopped)return;const p=await updateLiveJobDetail(id);if(p?.finished&&timer){clearInterval(timer);timer=null}};let timer=setInterval(tick,650);MODAL_CLEANUP=()=>{stopped=true;if(timer)clearInterval(timer)};await tick();
};

/* Password visibility for login, RCON and every game-config Password field. */
function enhancePasswordFields(root=document){
  root.querySelectorAll('input[type="password"]:not([data-eye-ready])').forEach(input=>{input.dataset.eyeReady='1';const wrap=document.createElement('div');wrap.className='password-wrap';input.parentNode.insertBefore(wrap,input);wrap.appendChild(input);const b=document.createElement('button');b.type='button';b.className='password-eye';b.textContent='◉';b.title='Show password';b.setAttribute('aria-label','Show password');globalThis.GSC_I18N?.attribute(b,'title','Show password');globalThis.GSC_I18N?.attribute(b,'aria-label','Show password');b.addEventListener('click',()=>{const show=input.type==='password';input.type=show?'text':'password';b.title=show?'Hide password':'Show password';b.setAttribute('aria-label',b.title);globalThis.GSC_I18N?.attribute(b,'title',show?'Hide password':'Show password');globalThis.GSC_I18N?.attribute(b,'aria-label',show?'Hide password':'Show password');b.textContent=show?'○':'◉'});wrap.appendChild(b)})
}
const baseOpenModal=openModal;openModal=function(...args){baseOpenModal(...args);enhancePasswordFields($('#modal'))};enhancePasswordFields(document);
new MutationObserver(()=>enhancePasswordFields(document)).observe(document.body,{childList:true,subtree:true});

/* Reconnect attached stdin consoles after lifecycle changes performed from the workspace. */
const baseServerAction=action;
const SERVER_UPDATE_UI={ id:null, sequence:0, generation:null, polling:false, timer:null };

function serverWorkspaceStopUpdatePolling() {
  if (SERVER_UPDATE_UI.timer !== null) {
    clearInterval(SERVER_UPDATE_UI.timer);
    SERVER_UPDATE_UI.timer = null;
  }
}
function serverWorkspaceStartUpdatePolling(id) {
  serverWorkspaceStopUpdatePolling();
  // Keep the console live even for scheduled updates started outside this
  // browser session. This timer exists only while Updates is visible.
  void serverWorkspacePollUpdateConsole(id, true);
  SERVER_UPDATE_UI.timer = setInterval(() => {
    if (SERVER_WORKSPACE.serverId === id && SERVER_WORKSPACE.tab === 'updates')
      void serverWorkspacePollUpdateConsole(id);
    else serverWorkspaceStopUpdatePolling();
  }, 1200);
}

async function serverWorkspacePollUpdateConsole(id, reset=false) {
  if(SERVER_WORKSPACE.serverId!==id || SERVER_WORKSPACE.tab!=='updates' || isReadOnly())return;
  const terminal=$('#swUpdateTerminal'), state=$('#swUpdateState');
  if(!terminal)return;
  if(reset || SERVER_UPDATE_UI.id!==id){
    SERVER_UPDATE_UI.id=id; SERVER_UPDATE_UI.sequence=0; SERVER_UPDATE_UI.generation=null;
    terminal.textContent='';
  }
  if(SERVER_UPDATE_UI.polling)return;
  SERVER_UPDATE_UI.polling=true;
  try{
    const result=await api(`/api/servers/${encodeURIComponent(id)}/update/console?since=${SERVER_UPDATE_UI.sequence}`);
    if(!terminal.isConnected || SERVER_WORKSPACE.serverId!==id || SERVER_WORKSPACE.tab!=='updates')return;
    if(SERVER_UPDATE_UI.generation!==result.generation){
      SERVER_UPDATE_UI.generation=result.generation;
      terminal.textContent='';
    }
    const messages=result.lines||[];
    if(messages.length && terminal.textContent==='No SteamCMD output available yet.')
      terminal.textContent='';
    for(const line of messages){
      const timestamp=line.at?new Date(line.at).toLocaleTimeString():'';
      terminal.textContent+=(terminal.textContent?'\n':'')+(timestamp?'['+timestamp+'] ':'')+line.text;
    }
    if(terminal.textContent.length>175000)terminal.textContent=terminal.textContent.slice(-150000);
    if(messages.length)terminal.scrollTop=terminal.scrollHeight;
    SERVER_UPDATE_UI.sequence=Math.max(SERVER_UPDATE_UI.sequence,Number(result.latestSequence)||0);
    if(state)state.textContent=(result.status||'Idle')+
      (result.percent!=null?' · '+result.percent+'%':'')+' · '+(result.message||'');
    if(!terminal.textContent)terminal.textContent='No SteamCMD output available yet.';
  }catch(e){if(state?.isConnected)state.textContent='Console unavailable: '+e.message}
  finally{SERVER_UPDATE_UI.polling=false}
}

function serverWorkspaceFocusUpdate(id) {
  const target='#server/'+encodeURIComponent(id)+'/updates';
  if(location.hash!==target){
    if(SERVER_WORKSPACE.serverId===id)serverWorkspaceSetTab('updates');
    else openServerPage(id,'updates');
  }
  return location.hash===target;
}

action=async function(id,a){
  const def=SERVERS.find(x=>x.id===id);
  const expectsUpdate=a==='update' ||
    (a==='start'&&!!def?.autoUpdateOnStart) ||
    ((a==='restart'||a==='apply')&&!!def?.autoUpdateOnRestart);
  if(expectsUpdate && !serverWorkspaceFocusUpdate(id))return;
  // The Updates page owns a single live polling timer. Avoid competing
  // per-action timers that linger after navigating to another server.
  try{await baseServerAction(id,a)}
  finally{if(expectsUpdate)await serverWorkspacePollUpdateConsole(id)}
  if(!['start','restart','stop'].includes(a))return;
  if(SERVER_WORKSPACE.serverId!==id||SERVER_WORKSPACE.tab!=='console')return;
  const d=SERVERS.find(x=>x.id===id);if(!d||profileFor(d)?.caps?.console!=='stdin')return;
  await renderServerWorkspaceTab(d,'console');
};

/* Keep the complete-delete behavior introduced earlier. */
window.deleteServer=function(id){
  const d=SERVERS.find(x=>x.id===id);if(!d)return;const bareMetal=d.hostingMode!=='Vm',hasSaves=Array.isArray(d.saveDirs)&&d.saveDirs.length>0;
  const permanent=bareMetal&&supportsAgentFeature('server.delete.permanent')?`<div class="section" data-gsc-i18n="Delete server completely">Delete server completely</div><div class="callout" data-gsc-i18n="This permanently stops the server and deletes the entire server folder. This cannot be undone.">This permanently stops the server and deletes the entire server folder. This cannot be undone.</div><div class="kv compact"><div data-gsc-i18n="Folder to delete">Folder to delete</div><div class="mono">${esc(d.guestWorkingDir||'')}</div></div><div class="checkrow" style="margin-top:14px"><input id="deleteFinalBackup" type="checkbox" ${hasSaves?'checked':'disabled'}><label for="deleteFinalBackup">Create a final save backup before deletion${hasSaves?'':' (no save folders configured)'}</label></div><div class="field"><label data-gsc-i18n="Type the exact server name to confirm">Type the exact server name to confirm</label><input id="deleteConfirm" autocomplete="off" placeholder="${esc(d.name)}" ${uiAction('syncPermanentDeleteButton',[id],{event:'input'})}><div class="help">Required: <b>${esc(d.name)}</b></div></div><button id="permanentDeleteBtn" class="btn danger" disabled ${uiAction('permanentDeleteServer',[id])} data-gsc-i18n="Permanently delete server and files">Permanently delete server and files</button>`:`<div class="section" data-gsc-i18n="Delete server completely">Delete server completely</div><div class="callout info">Complete file deletion is disabled for this server. It requires BareMetal hosting and an explicitly configured Agent deletion root.</div>`;
  openModal('Delete — '+d.name,`<div class="section" data-gsc-i18n="Remove from GameServerControl">Remove from GameServerControl</div><div class="help" data-gsc-i18n="Removes only the server definition. All game files and saves remain untouched.">Removes only the server definition. All game files and saves remain untouched.</div><button class="btn" ${uiAction('removeServerOnly',[id])} data-gsc-i18n="Remove only">Remove only</button>${permanent}`,`<button class="btn" ${uiAction('closeModal')} data-gsc-i18n="Cancel">Cancel</button>`,true)
};
window.removeServerOnly=async function(id){const d=SERVERS.find(x=>x.id===id);if(!confirm(`Remove "${d?.name||id}" from GameServerControl?\n\nServer files will remain untouched.`))return;try{await api(`/api/servers/${encodeURIComponent(id)}`,{method:'DELETE'});setNotice('Server removed. Files were kept.','ok');closeModal();await loadServers()}catch(e){setNotice('Remove failed: '+e.message,'err')}};
window.syncPermanentDeleteButton=function(id){const d=SERVERS.find(x=>x.id===id),input=$('#deleteConfirm'),button=$('#permanentDeleteBtn');if(button)button.disabled=!d||!input||input.value!==d.name};
window.permanentDeleteServer=async function(id){const d=SERVERS.find(x=>x.id===id);if(!d)return;const confirmation=$('#deleteConfirm')?.value||'';if(confirmation!==d.name)return setNotice('Type the exact server name first.','err');const createFinalBackup=!!$('#deleteFinalBackup')?.checked,button=$('#permanentDeleteBtn');if(button){button.disabled=true;button.textContent='Deleting…'}try{const result=await api(`/api/servers/${encodeURIComponent(id)}/delete-completely`,{method:'POST',body:JSON.stringify({confirmation,createFinalBackup})});setNotice(result.message||'Server permanently deleted.','ok');closeModal();await loadServers()}catch(e){setNotice('Permanent deletion cancelled/failed: '+e.message,'err');if(button){button.textContent='Permanently delete server and files';syncPermanentDeleteButton(id)}}};
