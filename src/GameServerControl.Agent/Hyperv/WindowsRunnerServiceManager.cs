using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

public sealed class WindowsRunnerServiceManager
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly WindowsRunnerPipeClient _client;
    private readonly ILogger<WindowsRunnerServiceManager> _logger;
    private readonly ConcurrentDictionary<string, string> _provisionedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _provisionGates = new(StringComparer.OrdinalIgnoreCase);

    public WindowsRunnerServiceManager(WindowsRunnerPipeClient client, ILogger<WindowsRunnerServiceManager> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool Supports(ServerDef def) => OperatingSystem.IsWindows() && def.HostingMode == HostingMode.BareMetal;

    public async Task<RunnerResponse> StartAsync(ServerDef def, CancellationToken ct = default)
    {
        var provision = await EnsureProvisionedAsync(def, ct);
        if (!provision.Ok) return new RunnerResponse(false, provision.Error);
        var started = await EnsureRunnerServiceRunningAsync(provision.Identity, ct);
        if (!started.Ok) return new RunnerResponse(false, started.Error);
        return await SendWithRetryAsync(provision.Identity, new RunnerRequest(RunnerCommand.Start), ct);
    }

    public async Task<RunnerResponse> FindAsync(ServerDef def, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.Find), ct);

    public async Task<RunnerResponse> StopAsync(ServerDef def, bool force, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.Stop, Force: force), ct);

    public async Task<RunnerResponse> ConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.ConsoleState), ct);

    internal Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        _client.SendAsync(RunnerIdentity.ForServer(def.Id), new RunnerRequest(RunnerCommand.ConsoleState), ct);

    public async Task<RunnerResponse> SendConsoleInputAsync(ServerDef def, string command, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: command), ct);

    public async Task<RunnerResponse> KillExtrasAsync(ServerDef def, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.KillExtras), ct);

    public async Task<bool?> GetAutostartAsync(ServerDef def, CancellationToken ct = default)
    {
        if (!Supports(def)) return null;
        var identity = RunnerIdentity.ForServer(def.Id);
        var manifestPath = ManifestPath(identity);
        if (File.Exists(manifestPath))
        {
            var manifest = await ReadManifestAsync(manifestPath, ct);
            if (manifest is not null) return manifest.AutoStart;
        }
        if (!string.IsNullOrWhiteSpace(def.ScheduledTaskName))
            return await QueryLegacyTaskEnabledAsync(def.ScheduledTaskName, ct);
        return false;
    }

    public async Task<(bool Ok, string Error)> SetAutostartAsync(ServerDef def, bool enabled, CancellationToken ct = default)
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

    private async Task<RunnerResponse> SendForServerAsync(ServerDef def, RunnerRequest request, CancellationToken ct)
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

    private async Task<ProvisionResult> EnsureProvisionedAsync(ServerDef def, CancellationToken ct)
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

        var existingManifest = await ReadManifestAsync(manifestPath, ct);
        var autoStart = existingManifest?.AutoStart ?? false;
        var firstProvision = existingManifest is null;
        if (firstProvision && !string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        {
            var legacy = await QueryLegacyTaskEnabledAsync(def.ScheduledTaskName, ct);
            if (legacy is null)
                return ProvisionResult.Fail("Could not read legacy Scheduled Task state before Runner migration.");
            autoStart = legacy.Value;
        }

        var interactive = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;
        var manifest = new RunnerManifest(
            def.Id,
            def.GuestExePath,
            def.GuestWorkingDir,
            def.StartArgs ?? Array.Empty<string>(),
            interactive,
            def.SteamAppId,
            def.StopProcessNames,
            autoStart);
        if (manifest.Validate() is { } validation) return ProvisionResult.Fail(validation);

        await WriteManifestAsync(manifestPath, manifest, ct);

        var serviceAccount = "NT SERVICE\\" + identity.ServiceName;
        var binPath = $"\"{runnerExe}\" --manifest \"{manifestPath}\"";
        var startMode = autoStart ? "auto" : "demand";
        var query = await RunToolAsync("sc.exe", ["query", identity.ServiceName], ToolTimeout, ct);
        ToolResult serviceResult;
        if (!query.Ok)
        {
            serviceResult = await RunToolAsync("sc.exe", [
                "create", identity.ServiceName,
                "binPath=", binPath,
                "start=", startMode,
                "obj=", serviceAccount,
                "DisplayName=", "GameServerControl Runner " + identity.Key
            ], ToolTimeout, ct);
        }
        else
        {
            serviceResult = await RunToolAsync("sc.exe", [
                "config", identity.ServiceName,
                "binPath=", binPath,
                "start=", startMode,
                "obj=", serviceAccount
            ], ToolTimeout, ct);
        }
        if (!serviceResult.Ok) return ProvisionResult.Fail("Runner service provisioning failed: " + serviceResult.Output);

        var acl = await ReconcileAclsAsync(def, identity, stateDirectory, ct);
        if (!acl.Ok) return ProvisionResult.Fail(acl.Error);

        if (firstProvision && !string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        {
            var migrated = await DisableLegacyTaskAsync(def.ScheduledTaskName, ct);
            if (!migrated.Ok) return ProvisionResult.Fail(migrated.Error);
        }

            var compatibility = await EnsureSatisfactoryCompatibilityAsync(def, identity, manifest, ct);
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

    private async Task<(bool Ok, string Error)> ReconcileAclsAsync(
        ServerDef def,
        RunnerIdentityInfo identity,
        string stateDirectory,
        CancellationToken ct)
    {
        var serviceAccount = "NT SERVICE\\" + identity.ServiceName;
        var runnerRoot = RunnerRoot();

        var stateInheritance = await RunToolAsync("icacls.exe", [stateDirectory, "/inheritance:r"], ToolTimeout, ct);
        if (!stateInheritance.Ok) return (false, "Could not protect Runner state directory: " + stateInheritance.Output);
        var stateGrant = await RunToolAsync("icacls.exe", [
            stateDirectory,
            "/grant:r",
            "*S-1-5-18:(OI)(CI)(F)",
            "*S-1-5-32-544:(OI)(CI)(F)",
            serviceAccount + ":(OI)(CI)(R)"
        ], ToolTimeout, ct);
        if (!stateGrant.Ok) return (false, "Could not grant Runner state access: " + stateGrant.Output);

        var runnerGrant = await RunToolAsync("icacls.exe", [runnerRoot, "/grant", serviceAccount + ":(OI)(CI)(RX)"], ToolTimeout, ct);
        if (!runnerGrant.Ok) return (false, "Could not grant Runner payload access: " + runnerGrant.Output);

        var workingGrant = await GrantModifyAsync(def.GuestWorkingDir, serviceAccount, ct);
        if (!workingGrant.Ok) return workingGrant;

        foreach (var saveDir in def.SaveDirs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(saveDir) || IsPathWithin(saveDir, def.GuestWorkingDir)) continue;
            if (def.SteamAppId == "1690800" && PathsEqual(saveDir, SatisfactoryLegacyRoot())) continue;
            if (IsDangerousGrantPath(saveDir))
                return (false, $"Refusing to grant Runner modify access to protected path: {saveDir}");
            Directory.CreateDirectory(saveDir);
            var grant = await GrantModifyAsync(saveDir, serviceAccount, ct);
            if (!grant.Ok) return grant;
        }

        return (true, "");
    }

    private async Task<(bool Ok, string Error)> GrantModifyAsync(string path, string serviceAccount, CancellationToken ct)
    {
        if (IsDangerousGrantPath(path)) return (false, $"Refusing to grant Runner modify access to protected path: {path}");
        var result = await RunToolAsync("icacls.exe", [path, "/grant", serviceAccount + ":(OI)(CI)(M)"], ToolTimeout, ct);
        return result.Ok ? (true, "") : (false, "Could not grant Runner game access: " + result.Output);
    }

    private async Task<(bool Ok, string Error)> EnsureSatisfactoryCompatibilityAsync(
        ServerDef def,
        RunnerIdentityInfo identity,
        RunnerManifest manifest,
        CancellationToken ct)
    {
        if (def.SteamAppId != "1690800") return (true, "");
        var marker = Path.Combine(StateDirectory(identity), ".satisfactory-profile-linked");
        if (File.Exists(marker)) return (true, "");

        var manifestPath = ManifestPath(identity);
        var wasRunning = await IsRunnerServiceRunningAsync(identity, ct);
        if (!wasRunning && manifest.AutoStart)
            await WriteManifestAsync(manifestPath, manifest with { AutoStart = false }, ct);

        var started = await EnsureRunnerServiceRunningAsync(identity, ct);
        if (!started.Ok) return started;
        var ping = await SendWithRetryAsync(identity, new RunnerRequest(RunnerCommand.Ping), ct);
        if (!ping.Ok || string.IsNullOrWhiteSpace(ping.LocalApplicationData))
            return (false, "Could not resolve Satisfactory Runner profile: " + ping.Error);

        var target = SatisfactoryLegacyRoot();
        Directory.CreateDirectory(target);
        var serviceAccount = "NT SERVICE\\" + identity.ServiceName;
        var targetGrant = await RunToolAsync("icacls.exe", [target, "/grant", serviceAccount + ":(OI)(CI)(M)"], ToolTimeout, ct);
        if (!targetGrant.Ok) return (false, "Could not grant Satisfactory legacy save access: " + targetGrant.Output);

        var link = Path.Combine(ping.LocalApplicationData, "FactoryGame");
        Directory.CreateDirectory(ping.LocalApplicationData);
        if (Directory.Exists(link))
        {
            var info = new DirectoryInfo(link);
            if (info.LinkTarget is null)
            {
                if (Directory.EnumerateFileSystemEntries(link).Any())
                    return (false, "Runner Satisfactory profile already contains non-empty FactoryGame data; refusing to overwrite it.");
                Directory.Delete(link);
            }
            else
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (resolved is not null && !PathsEqual(resolved, target))
                    return (false, "Runner Satisfactory profile points to a different FactoryGame target.");
            }
        }
        if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, target);
        await File.WriteAllTextAsync(marker, "linked", ct);

        if (!wasRunning)
        {
            await StopRunnerServiceAsync(identity, ct);
            if (manifest.AutoStart) await WriteManifestAsync(manifestPath, manifest, ct);
        }
        return (true, "");
    }

    private async Task<(bool Ok, string Error)> DisableLegacyTaskAsync(string taskName, CancellationToken ct)
    {
        var disabled = await RunToolAsync("schtasks.exe", ["/change", "/tn", taskName, "/disable"], ToolTimeout, ct);
        if (!disabled.Ok) return (false, "Could not disable legacy Scheduled Task: " + disabled.Output);
        _ = await RunToolAsync("schtasks.exe", ["/end", "/tn", taskName], ToolTimeout, ct);
        return (true, "");
    }

    private async Task<bool?> QueryLegacyTaskEnabledAsync(string taskName, CancellationToken ct)
    {
        var result = await RunToolAsync("schtasks.exe", ["/query", "/tn", taskName, "/xml"], ToolTimeout, ct);
        if (!result.Ok) return null;
        try
        {
            var doc = XDocument.Parse(result.Output);
            var enabled = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Enabled")?.Value;
            return bool.TryParse(enabled, out var value) ? value : null;
        }
        catch { return null; }
    }

    private async Task<(bool Ok, string Error)> EnsureRunnerServiceRunningAsync(RunnerIdentityInfo identity, CancellationToken ct)
    {
        if (await IsRunnerServiceRunningAsync(identity, ct)) return (true, "");
        var result = await RunToolAsync("sc.exe", ["start", identity.ServiceName], ToolTimeout, ct);
        if (!result.Ok && !result.Output.Contains("1056", StringComparison.OrdinalIgnoreCase))
            return (false, "Could not start Runner service: " + result.Output);
        return (true, "");
    }

    private async Task<bool> IsRunnerServiceRunningAsync(RunnerIdentityInfo identity, CancellationToken ct)
    {
        var result = await RunToolAsync("sc.exe", ["query", identity.ServiceName], ToolTimeout, ct);
        return result.Ok && result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StopRunnerServiceAsync(RunnerIdentityInfo identity, CancellationToken ct)
    {
        var result = await RunToolAsync("sc.exe", ["stop", identity.ServiceName], ToolTimeout, ct);
        if (!result.Ok && !result.Output.Contains("1062", StringComparison.OrdinalIgnoreCase))
            _logger.LogDebug("Runner service stop returned: {Output}", result.Output);
    }

    private static async Task<RunnerManifest?> ReadManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<RunnerManifest>(json, ManifestJson);
        }
        catch { return null; }
    }

    private static async Task WriteManifestAsync(string path, RunnerManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, ManifestJson), ct);
        File.Move(temp, path, overwrite: true);
    }

    private static string RunnerRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Runner"));

    private static string RunnerExecutablePath() => Path.Combine(RunnerRoot(), "GameServerControl.Runner.exe");

    private static string StateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GameServerControl",
        "Runners");

    private static string StateDirectory(RunnerIdentityInfo identity) => Path.Combine(StateRoot(), identity.StateDirectoryName);
    private static string ManifestPath(RunnerIdentityInfo identity) => Path.Combine(StateDirectory(identity), "runner.json");
    private static string SatisfactoryLegacyRoot() => @"C:\Windows\System32\config\systemprofile\AppData\Local\FactoryGame";

    private static bool IsDangerousGrantPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsPathWithin(full, windows) && !PathsEqual(full, SatisfactoryLegacyRoot())) return true;
            var product = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            if (IsPathWithin(full, product)) return true;
            var programDataState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GameServerControl");
            return IsPathWithin(full, programDataState);
        }
        catch { return true; }
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        try
        {
            var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                   || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static async Task<ToolResult> RunToolAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process is null) return new ToolResult(false, fileName + " returned no process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ToolResult(false, fileName + " timed out.");
            }
            var output = (await stdout) + (await stderr);
            return new ToolResult(process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex) { return new ToolResult(false, ex.Message); }
    }

    private sealed record ToolResult(bool Ok, string Output);

    private sealed record ProvisionResult(bool Ok, RunnerIdentityInfo Identity, string Error)
    {
        public static ProvisionResult Success(RunnerIdentityInfo identity) => new(true, identity, "");
        public static ProvisionResult Fail(string error) => new(false, new RunnerIdentityInfo("", "", "", ""), error);
    }
}
