from pathlib import Path
import re

local_path = Path('src/GameServerControl.Agent/Hyperv/LocalProcessService.cs')
text = local_path.read_text()

text = text.replace(
    '    private readonly IHubContext<StatusHub> _hub;\n    private readonly ConcurrentDictionary<string, ConsoleSession> _consoleSessions',
    '    private readonly IHubContext<StatusHub> _hub;\n    private readonly WindowsRunnerServiceManager _windowsRunner;\n    private readonly ConcurrentDictionary<string, ConsoleSession> _consoleSessions',
    1)
text = text.replace(
    '        ILogger<LocalProcessService> logger,\n        ServerRegistry registry,\n        IHubContext<StatusHub> hub)\n    {\n        _logger = logger;\n        _registry = registry;\n        _hub = hub;\n    }',
    '        ILogger<LocalProcessService> logger,\n        ServerRegistry registry,\n        IHubContext<StatusHub> hub,\n        WindowsRunnerServiceManager windowsRunner)\n    {\n        _logger = logger;\n        _registry = registry;\n        _hub = hub;\n        _windowsRunner = windowsRunner;\n    }',
    1)

old_start = '    public Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)\n    {\n        var def = FindServerForExe(exePath);'
new_start = '''    public async Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        var def = FindServerForExe(exePath);
        if (OperatingSystem.IsWindows())
        {
            if (def is null) return (false, null, "Refusing unregistered Windows BareMetal process launch.");
            if (!SetFirewallState(def, open: true, out var firewallError))
                return (false, null, "Could not prepare Windows Firewall: " + firewallError);

            var result = await _windowsRunner.StartAsync(def, ct);
            if (!result.Ok)
            {
                SetFirewallState(def, open: false, out _);
                return (false, null, result.Error ?? "Runner process launch failed.");
            }
            return (true, result.Pid, "");
        }

        return await StartLocalProcessAsync(exePath, args, workingDir, ct);
    }

    private Task<(bool Ok, int? Pid, string Error)> StartLocalProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Direct game process launch is disabled on Windows.");
        var def = FindServerForExe(exePath);'''
if old_start not in text:
    raise SystemExit('StartProcessAsync fragment not found')
text = text.replace(old_start, new_start, 1)

old_console = '    public LocalConsoleState GetConsoleState(ServerDef def)\n    {\n        var supported = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;'
new_console = '''    public async Task<LocalConsoleState> GetConsoleStateAsync(ServerDef def, CancellationToken ct = default)
    {
        var supported = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;
        if (!supported)
            return new LocalConsoleState(false, false, "This game profile does not use a stdin console.", Array.Empty<LogLine>());
        if (def.HostingMode != HostingMode.BareMetal)
            return new LocalConsoleState(true, false, "Interactive stdin is currently available only for servers running on this machine.", Array.Empty<LogLine>());

        if (OperatingSystem.IsWindows())
        {
            var response = await _windowsRunner.ConsoleStateAsync(def, ct);
            if (!response.Ok)
                return new LocalConsoleState(true, false, response.Error ?? "Runner console is unavailable.", Array.Empty<LogLine>());
            var snapshot = response.Console;
            if (snapshot is null)
                return new LocalConsoleState(true, false, "Runner returned no console state.", Array.Empty<LogLine>());
            var lines = snapshot.Lines.Select(line => new LogLine(def.Id, line.At, line.Source, line.Text)).ToArray();
            return new LocalConsoleState(snapshot.Supported, snapshot.InputAvailable, snapshot.Message, lines);
        }

        return GetLocalConsoleState(def);
    }

    private LocalConsoleState GetLocalConsoleState(ServerDef def)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Direct console attachment is disabled on Windows.");
        var supported = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;'''
if old_console not in text:
    raise SystemExit('GetConsoleState fragment not found')
text = text.replace(old_console, new_console, 1)

send_marker = '''        if (def.HostingMode != HostingMode.BareMetal)
            return (false, "Interactive stdin is not available for Hyper-V guest processes yet.");
        if (!_consoleSessions.TryGetValue(def.Id, out var session) || !session.InputAvailable)'''
send_repl = '''        if (def.HostingMode != HostingMode.BareMetal)
            return (false, "Interactive stdin is not available for Hyper-V guest processes yet.");
        if (OperatingSystem.IsWindows())
        {
            var result = await _windowsRunner.SendConsoleInputAsync(def, command, ct);
            return (result.Ok, result.Error ?? "");
        }
        if (!_consoleSessions.TryGetValue(def.Id, out var session) || !session.InputAvailable)'''
if send_marker not in text:
    raise SystemExit('SendConsoleInput insertion point not found')
text = text.replace(send_marker, send_repl, 1)

old_find = '    public Task<(bool Ok, int? Pid)> FindProcessAsync(string exePath, CancellationToken ct = default)\n    {\n        var def = FindServerForExe(exePath);\n        var processImage = ResolveProcessImagePath(exePath);'
new_find = '''    public async Task<(bool Ok, int? Pid)> FindProcessAsync(string exePath, CancellationToken ct = default)
    {
        var def = FindServerForExe(exePath);
        if (OperatingSystem.IsWindows())
        {
            if (def is null) return (false, null);
            var result = await _windowsRunner.FindAsync(def, ct);
            if (!result.Ok) return (false, null);
            SetFirewallState(def, open: result.Pid is not null, out _);
            return (true, result.Pid);
        }
        return await FindLocalProcessAsync(exePath, ct);
    }

    private Task<(bool Ok, int? Pid)> FindLocalProcessAsync(string exePath, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Direct game process discovery is disabled on Windows.");
        var def = FindServerForExe(exePath);
        var processImage = ResolveProcessImagePath(exePath);'''
if old_find not in text:
    raise SystemExit('FindProcessAsync fragment not found')
text = text.replace(old_find, new_find, 1)

stop_marker = '''        var def = FindServerForExe(exePath);
        var processImage = ResolveProcessImagePath(exePath);
        try
        {
            Process? p = null;'''
stop_repl = '''        var def = FindServerForExe(exePath);
        var processImage = ResolveProcessImagePath(exePath);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (def is null) return false;
                var result = await _windowsRunner.StopAsync(def, force, ct);
                if (result.Ok) SetFirewallState(def, open: false, out _);
                return result.Ok;
            }

            Process? p = null;'''
stop_at = text.find('    public async Task<bool> StopProcessAsync')
marker_at = text.find(stop_marker, stop_at)
if marker_at < 0:
    raise SystemExit('StopProcessAsync insertion point not found')
text = text[:marker_at] + text[marker_at:].replace(stop_marker, stop_repl, 1)

old_kill = '''    public Task KillByNamesAsync(IEnumerable<string> exeNames, CancellationToken ct = default)
    {
        foreach (var raw in exeNames.Distinct(StringComparer.OrdinalIgnoreCase))'''
new_kill = '''    public async Task KillByNamesAsync(ServerDef def, IEnumerable<string> exeNames, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await _windowsRunner.KillExtrasAsync(def, ct);
            if (!result.Ok) _logger.LogWarning("Runner extra-process cleanup failed for {ServerId}: {Error}", def.Id, result.Error);
            return;
        }

        foreach (var raw in exeNames.Distinct(StringComparer.OrdinalIgnoreCase))'''
if old_kill not in text:
    raise SystemExit('KillByNamesAsync fragment not found')
text = text.replace(old_kill, new_kill, 1)
text = text.replace(
    '        return Task.CompletedTask;\n    }\n\n    public async Task<(bool Ok, string Output)> RunCommandAsync',
    '    }\n\n    public async Task<(bool Ok, string Output)> RunCommandAsync',
    1)
local_path.write_text(text)

orch = Path('src/GameServerControl.Agent/Servers/ServerOrchestrator.cs')
text = orch.read_text()
start_pattern = re.compile(r'''\n        if \(!string\.IsNullOrWhiteSpace\(def\.ScheduledTaskName\) && _local\.HasScheduledTaskSupport\)\n        \{.*?\n        \}\n\n        await LogAsync\(def\.Id, "orch", "Launching "''', re.S)
text, count = start_pattern.subn('\n        await LogAsync(def.Id, "orch", "Launching "', text, count=1)
if count != 1:
    raise SystemExit(f'expected one scheduled-task start branch, got {count}')
stop_pattern = re.compile(r'''\n            if \(!string\.IsNullOrWhiteSpace\(def\.ScheduledTaskName\) && _local\.HasScheduledTaskSupport\)\n            \{.*?\n            \}\n\n            var pid = _tracker\.Get\(id\)\.PidInGuest;''', re.S)
text, count = stop_pattern.subn('\n            var pid = _tracker.Get(id).PidInGuest;', text, count=1)
if count != 1:
    raise SystemExit(f'expected one scheduled-task stop branch, got {count}')
text = text.replace('await _local.KillByNamesAsync(extras, ct);', 'await _local.KillByNamesAsync(def, extras, ct);', 1)
orch.write_text(text)

status = Path('src/GameServerControl.Agent/Hubs/StatusHub.cs')
text = status.read_text()
text = text.replace('var state = _local.GetConsoleState(def);', 'var state = await _local.GetConsoleStateAsync(def, Context.ConnectionAborted);', 1)
status.write_text(text)

program = Path('src/GameServerControl.Agent/Program.cs')
text = program.read_text()
text = text.replace(
    'builder.Services.AddSingleton<GuestProcessService>();\nbuilder.Services.AddSingleton<LocalProcessService>();',
    'builder.Services.AddSingleton<GuestProcessService>();\nbuilder.Services.AddSingleton<WindowsRunnerPipeClient>();\nbuilder.Services.AddSingleton<WindowsRunnerServiceManager>();\nbuilder.Services.AddSingleton<LocalProcessService>();',
    1)
old_auto = '''api.MapGet("/servers/{id}/autostart", async (string id, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.Ok(new { supported = false, enabled = (bool?)null });
    var enabled = await local.GetAutostartAsync(def.ScheduledTaskName, ct);
    return Results.Ok(new { supported = true, enabled });
});
api.MapPost("/servers/{id}/autostart", async (string id, AutostartRequest req, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.BadRequest(new { error = "Server has no ScheduledTaskName set." });
    var ok = await local.SetAutostartAsync(def.ScheduledTaskName, req.Enabled, ct);
    return ok ? Results.Ok(new { enabled = req.Enabled }) : Results.StatusCode(502);
});'''
new_auto = '''api.MapGet("/servers/{id}/autostart", async (string id, ServerRegistry reg, WindowsRunnerServiceManager runners, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (!runners.Supports(def))
        return Results.Ok(new { supported = false, enabled = (bool?)null });
    var enabled = await runners.GetAutostartAsync(def, ct);
    return Results.Ok(new { supported = true, enabled });
});
api.MapPost("/servers/{id}/autostart", async (string id, AutostartRequest req, ServerRegistry reg, WindowsRunnerServiceManager runners, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (!runners.Supports(def))
        return Results.BadRequest(new { error = "Autostart is supported for Windows BareMetal Runner servers." });
    var (ok, error) = await runners.SetAutostartAsync(def, req.Enabled, ct);
    return ok ? Results.Ok(new { enabled = req.Enabled }) : Results.BadRequest(new { error });
});'''
if old_auto not in text:
    raise SystemExit('Program autostart block not found')
text = text.replace(old_auto, new_auto, 1)
program.write_text(text)

contract = Path('scripts/validate-arch01-runner.mjs')
text = contract.read_text()
text = text.replace(
    "requireCheck(!/OperatingSystem\\.IsWindows\\(\\)[\\s\\S]{0,2200}?Process\\.Start\\s*\\(/m.test(local), 'Windows LocalProcessService still has a direct game Process.Start fallback.');",
    "requireCheck(local.includes('_windowsRunner.StartAsync(def, ct)'), 'Windows start path must delegate to the Runner.');\nrequireCheck(/StartLocalProcessAsync[\\s\\S]{0,500}?OperatingSystem\\.IsWindows\\(\\)[^\\n]*throw/m.test(local), 'Direct process launch helper must fail closed on Windows.');")
text = text.replace(
    "requireCheck(manager.includes('GameServerControlRunner_'), 'Runner service names must use the deterministic GameServerControlRunner_ prefix.');\n",
    '')
contract.write_text(text)
