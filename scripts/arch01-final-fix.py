from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"expected fragment not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1))


client_path = "src/GameServerControl.Agent/Hyperv/WindowsRunnerPipeClient.cs"
client = Path(client_path).read_text()
client = client.replace(
    "namespace GameServerControl.Agent.Hyperv;\n\npublic sealed class WindowsRunnerPipeClient",
    "namespace GameServerControl.Agent.Hyperv;\n\ninternal readonly record struct RunnerPipeResult(RunnerResponse Response, bool TransportFailure);\n\npublic sealed class WindowsRunnerPipeClient",
    1,
)
old_send = '''    public async Task<RunnerResponse> SendAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
    {
        var validation = request.Validate();
        if (validation is not null) return new RunnerResponse(false, validation);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, _json);
        if (payload.Length > RunnerProtocolLimits.MaxRequestBytes)
            return new RunnerResponse(false, "Runner request exceeded the protocol limit.");

        try
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
            return JsonSerializer.Deserialize<RunnerResponse>(responseBytes, _json)
                   ?? new RunnerResponse(false, "Runner returned an empty response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RunnerResponse(false, "Runner pipe connection timed out.");
        }
        catch (Exception ex)
        {
            return new RunnerResponse(false, "Runner IPC failed: " + ex.Message);
        }
    }
'''
new_send = '''    public async Task<RunnerResponse> SendAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
        => (await SendDetailedAsync(identity, request, ct)).Response;

    internal async Task<RunnerPipeResult> SendDetailedAsync(
        RunnerIdentityInfo identity,
        RunnerRequest request,
        CancellationToken ct = default)
    {
        var validation = request.Validate();
        if (validation is not null)
            return new RunnerPipeResult(new RunnerResponse(false, validation), TransportFailure: false);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, _json);
        if (payload.Length > RunnerProtocolLimits.MaxRequestBytes)
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner request exceeded the protocol limit."),
                TransportFailure: false);

        try
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
            var response = JsonSerializer.Deserialize<RunnerResponse>(responseBytes, _json)
                           ?? new RunnerResponse(false, "Runner returned an empty response.");
            return new RunnerPipeResult(response, TransportFailure: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner pipe connection timed out."),
                TransportFailure: true);
        }
        catch (Exception ex)
        {
            return new RunnerPipeResult(
                new RunnerResponse(false, "Runner IPC failed: " + ex.Message),
                TransportFailure: true);
        }
    }
'''
if old_send not in client:
    raise SystemExit("WindowsRunnerPipeClient SendAsync block not found")
Path(client_path).write_text(client.replace(old_send, new_send, 1))

manager_path = "src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs"
manager = Path(manager_path).read_text()
manager = manager.replace(
    "using System.Diagnostics;\nusing System.Text.Json;",
    "using System.Collections.Concurrent;\nusing System.Diagnostics;\nusing System.Security.Cryptography;\nusing System.Text;\nusing System.Text.Json;",
    1,
)
manager = manager.replace(
    "    private readonly WindowsRunnerPipeClient _client;\n    private readonly ILogger<WindowsRunnerServiceManager> _logger;",
    "    private readonly WindowsRunnerPipeClient _client;\n    private readonly ILogger<WindowsRunnerServiceManager> _logger;\n    private readonly ConcurrentDictionary<string, string> _provisionedFingerprints = new(StringComparer.OrdinalIgnoreCase);\n    private readonly ConcurrentDictionary<string, SemaphoreSlim> _provisionGates = new(StringComparer.OrdinalIgnoreCase);",
    1,
)
old_send_for = '''    private async Task<RunnerResponse> SendForServerAsync(ServerDef def, RunnerRequest request, CancellationToken ct)
    {
        var provision = await EnsureProvisionedAsync(def, ct);
        if (!provision.Ok) return new RunnerResponse(false, provision.Error);
        var started = await EnsureRunnerServiceRunningAsync(provision.Identity, ct);
        if (!started.Ok) return new RunnerResponse(false, started.Error);
        return await SendWithRetryAsync(provision.Identity, request, ct);
    }

    private async Task<RunnerResponse> SendWithRetryAsync(RunnerIdentityInfo identity, RunnerRequest request, CancellationToken ct)
    {
        RunnerResponse last = new(false, "Runner did not respond.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last = await _client.SendAsync(identity, request, ct);
            if (last.Ok || !last.Error?.Contains("IPC", StringComparison.OrdinalIgnoreCase) == true) return last;
            if (attempt < 2) await Task.Delay(250, ct);
        }
        return last;
    }
'''
new_send_for = '''    private async Task<RunnerResponse> SendForServerAsync(ServerDef def, RunnerRequest request, CancellationToken ct)
    {
        var provision = await EnsureProvisionedAsync(def, ct);
        if (!provision.Ok) return new RunnerResponse(false, provision.Error);
        return await SendWithRetryAsync(provision.Identity, request, ct);
    }

    private async Task<RunnerResponse> SendWithRetryAsync(RunnerIdentityInfo identity, RunnerRequest request, CancellationToken ct)
    {
        RunnerResponse last = new(false, "Runner did not respond.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var send = await _client.SendDetailedAsync(identity, request, ct);
            last = send.Response;
            if (!IsTransportFailure(send)) return last;

            if (attempt == 0)
            {
                var started = await EnsureRunnerServiceRunningAsync(identity, ct);
                if (!started.Ok) return new RunnerResponse(false, started.Error);
            }
            else if (attempt == 1)
            {
                await StopRunnerServiceAsync(identity, ct);
                var restarted = await EnsureRunnerServiceRunningAsync(identity, ct);
                if (!restarted.Ok) return new RunnerResponse(false, restarted.Error);
            }

            if (attempt < 2) await Task.Delay(250, ct);
        }
        return last;
    }

    private static bool IsTransportFailure(RunnerPipeResult result) => result.TransportFailure;
'''
if old_send_for not in manager:
    raise SystemExit("WindowsRunnerServiceManager send/retry block not found")
manager = manager.replace(old_send_for, new_send_for, 1)

old_provision_start = '''    private async Task<ProvisionResult> EnsureProvisionedAsync(ServerDef def, CancellationToken ct)
    {
        if (!Supports(def)) return ProvisionResult.Fail("Windows Runner is not applicable to this server.");

        var identity = RunnerIdentity.ForServer(def.Id);
        var runnerExe = RunnerExecutablePath();
        if (!File.Exists(runnerExe))
            return ProvisionResult.Fail($"Runner executable is missing: {runnerExe}");
        if (string.IsNullOrWhiteSpace(def.GuestWorkingDir) || !Directory.Exists(def.GuestWorkingDir))
            return ProvisionResult.Fail("Server working directory does not exist.");

        Directory.CreateDirectory(StateRoot());
        var stateDirectory = StateDirectory(identity);
        Directory.CreateDirectory(stateDirectory);
        var manifestPath = ManifestPath(identity);
'''
new_provision_start = '''    private async Task<ProvisionResult> EnsureProvisionedAsync(ServerDef def, CancellationToken ct)
    {
        if (!Supports(def)) return ProvisionResult.Fail("Windows Runner is not applicable to this server.");

        var identity = RunnerIdentity.ForServer(def.Id);
        var fingerprint = ProvisionFingerprint(def);
        var manifestPath = ManifestPath(identity);
        if (IsProvisionCached(def.Id, fingerprint, manifestPath))
            return ProvisionResult.Success(identity);

        var provisionGate = _provisionGates.GetOrAdd(def.Id, _ => new SemaphoreSlim(1, 1));
        await provisionGate.WaitAsync(ct);
        try
        {
            if (IsProvisionCached(def.Id, fingerprint, manifestPath))
                return ProvisionResult.Success(identity);

            var runnerExe = RunnerExecutablePath();
            if (!File.Exists(runnerExe))
                return ProvisionResult.Fail($"Runner executable is missing: {runnerExe}");
            if (string.IsNullOrWhiteSpace(def.GuestWorkingDir) || !Directory.Exists(def.GuestWorkingDir))
                return ProvisionResult.Fail("Server working directory does not exist.");

            Directory.CreateDirectory(StateRoot());
            var stateDirectory = StateDirectory(identity);
            Directory.CreateDirectory(stateDirectory);
'''
if old_provision_start not in manager:
    raise SystemExit("EnsureProvisionedAsync start block not found")
manager = manager.replace(old_provision_start, new_provision_start, 1)

old_provision_end = '''        var compatibility = await EnsureSatisfactoryCompatibilityAsync(def, identity, manifest, ct);
        if (!compatibility.Ok) return ProvisionResult.Fail(compatibility.Error);

        return ProvisionResult.Success(identity);
    }

    private async Task<(bool Ok, string Error)> ReconcileAclsAsync('''
new_provision_end = '''            var compatibility = await EnsureSatisfactoryCompatibilityAsync(def, identity, manifest, ct);
            if (!compatibility.Ok) return ProvisionResult.Fail(compatibility.Error);

            _provisionedFingerprints[def.Id] = fingerprint;
            return ProvisionResult.Success(identity);
        }
        finally
        {
            provisionGate.Release();
        }
    }

    private bool IsProvisionCached(string serverId, string fingerprint, string manifestPath)
        => _provisionedFingerprints.TryGetValue(serverId, out var cached)
           && string.Equals(cached, fingerprint, StringComparison.Ordinal)
           && File.Exists(manifestPath)
           && File.Exists(RunnerExecutablePath());

    private static string ProvisionFingerprint(ServerDef def)
    {
        var interactive = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;
        var payload = JsonSerializer.Serialize(new
        {
            def.Id,
            def.GuestExePath,
            def.GuestWorkingDir,
            StartArgs = def.StartArgs ?? Array.Empty<string>(),
            def.SteamAppId,
            StopProcessNames = def.StopProcessNames ?? Array.Empty<string>(),
            SaveDirs = def.SaveDirs ?? Array.Empty<string>(),
            def.ScheduledTaskName,
            Interactive = interactive
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private async Task<(bool Ok, string Error)> ReconcileAclsAsync('''
if old_provision_end not in manager:
    raise SystemExit("EnsureProvisionedAsync end block not found")
manager = manager.replace(old_provision_end, new_provision_end, 1)
Path(manager_path).write_text(manager)
