from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"expected fragment not found in {path}: {old[:160]!r}")
    p.write_text(text.replace(old, new, 1))


client_path = 'src/GameServerControl.Agent/Hyperv/WindowsRunnerPipeClient.cs'
client = Path(client_path).read_text()
client = client.replace(
    '    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);',
    '    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);\n    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(12);',
    1)
old_try = '''        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token);

            await WriteFrameAsync(pipe, payload, ct);
            var responseBytes = await ReadFrameAsync(pipe, RunnerProtocolLimits.MaxResponseBytes, ct);
'''
new_try = '''        try
        {
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operationCts.CancelAfter(OperationTimeout);

            await using var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(operationCts.Token);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token);

            await WriteFrameAsync(pipe, payload, operationCts.Token);
            var responseBytes = await ReadFrameAsync(pipe, RunnerProtocolLimits.MaxResponseBytes, operationCts.Token);
'''
if old_try not in client:
    raise SystemExit('pipe operation block not found')
Path(client_path).write_text(client.replace(old_try, new_try, 1))

manager_path = 'src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs'
manager = Path(manager_path).read_text()
insert_after = '''    public async Task<(bool Ok, string Error)> SetAutostartAsync(ServerDef def, bool enabled, CancellationToken ct = default)
    {
        if (!Supports(def)) return (false, "Windows Runner autostart applies only to Windows BareMetal servers.");
        var provision = await EnsureProvisionedAsync(def, ct);
        if (!provision.Ok) return (false, provision.Error);

        var manifestPath = ManifestPath(provision.Identity);
        var manifest = await ReadManifestAsync(manifestPath, ct);
        if (manifest is null) return (false, "Runner manifest could not be read.");
        manifest = manifest with { AutoStart = enabled };
        await WriteManifestAsync(manifestPath, manifest, ct);

        var startMode = enabled ? "auto" : "demand";
        var configured = await RunToolAsync("sc.exe", ["config", provision.Identity.ServiceName, "start=", startMode], ToolTimeout, ct);
        return configured.Ok ? (true, "") : (false, configured.Output);
    }
'''
deprovision = insert_after + '''
    public async Task<(bool Ok, string Error)> DeprovisionAsync(ServerDef def, CancellationToken ct = default)
    {
        if (!Supports(def)) return (true, "");

        var identity = RunnerIdentity.ForServer(def.Id);
        var serviceAccount = "NT SERVICE\\\\" + identity.ServiceName;
        var provisionGate = _provisionGates.GetOrAdd(def.Id, _ => new SemaphoreSlim(1, 1));
        await provisionGate.WaitAsync(ct);
        try
        {
            var query = await RunToolAsync("sc.exe", ["query", identity.ServiceName], ToolTimeout, ct);
            var serviceExists = query.Ok || !query.Output.Contains("1060", StringComparison.OrdinalIgnoreCase);
            if (serviceExists)
            {
                var started = await EnsureRunnerServiceRunningAsync(identity, ct);
                if (!started.Ok) return (false, started.Error);

                var stopGame = await _client.SendDetailedAsync(
                    identity,
                    new RunnerRequest(RunnerCommand.Stop, Force: true),
                    ct);
                if (stopGame.TransportFailure)
                    return (false, "Could not contact Runner to stop the managed game before deprovisioning: " + stopGame.Response.Error);
                if (!stopGame.Response.Ok)
                    return (false, "Runner refused game stop before deprovisioning: " + stopGame.Response.Error);

                await StopRunnerServiceAsync(identity, ct);
            }

            foreach (var path in DeprovisionGrantPaths(def).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                var removed = await RemoveGrantAsync(path, serviceAccount, ct);
                if (!removed.Ok) return removed;
            }

            if (serviceExists)
            {
                var deleted = await RunToolAsync("sc.exe", ["delete", identity.ServiceName], ToolTimeout, ct);
                if (!deleted.Ok && !deleted.Output.Contains("1060", StringComparison.OrdinalIgnoreCase))
                    return (false, "Could not delete Runner service: " + deleted.Output);
            }

            var stateDirectory = StateDirectory(identity);
            if (Directory.Exists(stateDirectory))
            {
                try { Directory.Delete(stateDirectory, recursive: true); }
                catch (Exception ex) { return (false, "Could not remove Runner state directory: " + ex.Message); }
            }

            _provisionedFingerprints.TryRemove(def.Id, out _);
            return (true, "");
        }
        finally
        {
            provisionGate.Release();
        }
    }

    private static IEnumerable<string> DeprovisionGrantPaths(ServerDef def)
    {
        yield return RunnerRoot();
        yield return def.GuestWorkingDir;
        foreach (var saveDir in def.SaveDirs ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(saveDir) && !IsPathWithin(saveDir, def.GuestWorkingDir))
                yield return saveDir;
        if (def.SteamAppId == "1690800") yield return SatisfactoryLegacyRoot();
    }

    private static async Task<(bool Ok, string Error)> RemoveGrantAsync(string path, string serviceAccount, CancellationToken ct)
    {
        var result = await RunToolAsync("icacls.exe", [path, "/remove:g", serviceAccount], ToolTimeout, ct);
        return result.Ok ? (true, "") : (false, "Could not remove Runner ACL grant from " + path + ": " + result.Output);
    }
'''
if insert_after not in manager:
    raise SystemExit('SetAutostartAsync block not found')
Path(manager_path).write_text(manager.replace(insert_after, deprovision, 1))

program_path = 'src/GameServerControl.Agent/Program.cs'
program = Path(program_path).read_text()
old_delete = '''api.MapDelete("/servers/{id}", async (string id, ServerStore store, PublicRelayManager relay, CancellationToken ct) =>
{
    if (!store.Delete(id)) return Results.NotFound();
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();
});'''
new_delete = '''api.MapDelete("/servers/{id}", async (
    string id,
    ServerRegistry reg,
    ServerStore store,
    WindowsRunnerServiceManager runners,
    PublicRelayManager relay,
    CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();

    if (runners.Supports(def))
    {
        var cleanup = await runners.DeprovisionAsync(def, ct);
        if (!cleanup.Ok)
            return Results.Json(new { error = cleanup.Error }, statusCode: StatusCodes.Status502BadGateway);
    }

    if (!store.Delete(id)) return Results.NotFound();
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();
});'''
if old_delete not in program:
    raise SystemExit('server delete endpoint not found')
Path(program_path).write_text(program.replace(old_delete, new_delete, 1))
