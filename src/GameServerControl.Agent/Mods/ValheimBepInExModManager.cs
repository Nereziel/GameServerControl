using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Bare-metal Valheim BepInEx/Thunderstore manager. The first mod installation bootstraps the
/// current BepInExPack_Valheim into the server root, then installs plugins under BepInEx/plugins.
/// Hyper-V is deliberately rejected until guest-native mod transport is implemented; writing a
/// guest path on the host would be unsafe and misleading.
/// </summary>
public sealed class ValheimBepInExModManager : IModManager
{
    private const string BepInExOwner = "denikson";
    private const string BepInExPackage = "BepInExPack_Valheim";

    private readonly ILogger<ValheimBepInExModManager> _logger;
    private readonly HttpClient _http;
    private readonly ThunderstoreClient _thunderstore;
    private readonly ServerStore _store;
    private readonly LocalProcessService _local;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);

    public ValheimBepInExModManager(
        ILogger<ValheimBepInExModManager> logger,
        ThunderstoreClient thunderstore,
        ServerStore store,
        LocalProcessService local)
    {
        _logger = logger;
        _thunderstore = thunderstore;
        _store = store;
        _local = local;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl-ModInstaller/1.0");
    }

    public string? MarketplaceSource(ServerDef def) =>
        def.HostingMode == HostingMode.BareMetal ? "valheim.thunderstore.io" : null;

    public Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct) =>
        def.HostingMode == HostingMode.BareMetal
            ? _thunderstore.SearchAsync("valheim", query, limit, serverSideOnly, ct)
            : Task.FromResult(Array.Empty<ModSearchResult>());

    public bool Supports(ServerDef def) =>
        def.HostingMode == HostingMode.BareMetal &&
        def.SteamAppId == "896660" &&
        !string.IsNullOrWhiteSpace(def.GuestWorkingDir);

    public string ModsFolder(ServerDef def) => Path.Combine(def.GuestWorkingDir, "BepInEx", "plugins");
    private static string MetadataPath(ServerDef def) => Path.Combine(def.GuestWorkingDir, "BepInEx", ".gsc-mods.json");

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var dir = ModsFolder(def);
        if (!Directory.Exists(dir)) return Task.FromResult(Array.Empty<ModInfo>());

        var meta = LoadMetadata(def);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModInfo>();

        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileName(dll);
            seen.Add(id);
            var info = new FileInfo(dll);
            var sidecar = meta.GetValueOrDefault(id);
            result.Add(new ModInfo(id, sidecar?.DisplayName ?? Path.GetFileNameWithoutExtension(dll), sidecar?.Version,
                sidecar?.Source, sidecar?.InstalledAt ?? info.CreationTimeUtc, info.Length,
                new[] { Path.GetRelativePath(def.GuestWorkingDir, dll) }));
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            if (seen.Contains(id)) continue;
            var sidecar = meta.GetValueOrDefault(id);
            long size = 0;
            try { size = new DirectoryInfo(sub).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { }
            result.Add(new ModInfo(id, sidecar?.DisplayName ?? id, sidecar?.Version, sidecar?.Source,
                sidecar?.InstalledAt ?? Directory.GetCreationTimeUtc(sub), size, sidecar?.Files));
        }

        return Task.FromResult(result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct)
    {
        if (!Supports(def))
            return new ModInstallResult(false, null, "Valheim mod management is currently supported only for BareMetal servers.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new ModInstallResult(false, null, "URL must be an http(s) link to a zip.");

        var (lookupOk, runningPid) = await _local.FindProcessAsync(def.GuestExePath, ct);
        if (lookupOk && runningPid is not null)
            return new ModInstallResult(false, null, "Stop the Valheim server before installing or updating mods.");

        var bootstrap = await EnsureBepInExAsync(def, ct);
        if (!bootstrap.Ok) return new ModInstallResult(false, null, bootstrap.Error);
        def = bootstrap.Def;

        var parsedRequested = ThunderstoreClient.ParseDownloadUrl(url);
        if (parsedRequested is { owner: var owner, name: var name } &&
            owner.Equals(BepInExOwner, StringComparison.OrdinalIgnoreCase) &&
            name.Equals(BepInExPackage, StringComparison.OrdinalIgnoreCase))
            return new ModInstallResult(true, null, null);

        var modsDir = ModsFolder(def);
        Directory.CreateDirectory(modsDir);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "gsc-mod-" + Guid.NewGuid().ToString("n")[..8]);
        var zipPath = Path.Combine(tmpRoot, "download.zip");
        var extractPath = Path.Combine(tmpRoot, "extract");
        Directory.CreateDirectory(tmpRoot);

        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return new ModInstallResult(false, null, $"Download returned HTTP {(int)resp.StatusCode}.");
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            try { ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true); }
            catch (Exception ex) { return new ModInstallResult(false, null, "Not a valid zip: " + ex.Message); }

            var (sourceDir, copyDlls) = LocateModPayload(extractPath);
            if (sourceDir is null && copyDlls is null)
                return new ModInstallResult(false, null,
                    "Could not find plugin DLLs. Expected a Thunderstore plugins/ folder, top-level DLLs, or one DLL-containing folder.");

            string modId;
            var installedFiles = new List<string>();
            if (sourceDir is not null)
            {
                modId = SanitizeFolderName(displayName ?? Path.GetFileName(sourceDir.TrimEnd('\\', '/')) ?? "mod");
                var destDir = Path.Combine(modsDir, modId);
                if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                CopyDirectory(sourceDir, destDir);
                installedFiles.AddRange(Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(def.GuestWorkingDir, f)));
            }
            else
            {
                foreach (var dll in copyDlls!)
                {
                    var dst = Path.Combine(modsDir, Path.GetFileName(dll));
                    File.Copy(dll, dst, overwrite: true);
                    installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, dst));
                }
                modId = Path.GetFileName(copyDlls![0]);
            }

            string? version = null;
            string? finalName = displayName;
            string[] dependencies = Array.Empty<string>();
            var manifest = Directory.EnumerateFiles(extractPath, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
            if (manifest is not null)
            {
                try
                {
                    var json = JsonNode.Parse(await File.ReadAllTextAsync(manifest, ct));
                    version = json?["version_number"]?.ToString();
                    finalName ??= json?["name"]?.ToString();
                    if (json?["dependencies"] is JsonArray deps)
                        dependencies = deps.Select(d => d?.ToString() ?? "").Where(s => s.Length > 0).ToArray();
                }
                catch { }
            }
            finalName ??= modId;

            var info = new ModInfo(modId, finalName, version, url, DateTimeOffset.UtcNow,
                installedFiles.Select(f => new FileInfo(Path.Combine(def.GuestWorkingDir, f)).Length).Sum(), installedFiles.ToArray());
            UpdateMetadata(def, m => m[modId] = ToSidecar(info));

            foreach (var dep in dependencies)
            {
                ct.ThrowIfCancellationRequested();
                var parts = dep.Split('-');
                if (parts.Length < 3) continue;
                var depOwner = parts[0];
                var depName = string.Join('-', parts.Skip(1).Take(parts.Length - 2));
                if (depOwner.Equals(BepInExOwner, StringComparison.OrdinalIgnoreCase) &&
                    depName.Equals(BepInExPackage, StringComparison.OrdinalIgnoreCase))
                    continue;

                var depKey = SanitizeFolderName(depName);
                if (Directory.Exists(Path.Combine(modsDir, depKey)) || File.Exists(Path.Combine(modsDir, depKey + ".dll")))
                    continue;

                var pkg = await _thunderstore.FindPackageAsync("valheim", depOwner, depName, ct);
                var dlUrl = pkg?.Versions?.FirstOrDefault()?.DownloadUrl;
                if (string.IsNullOrWhiteSpace(dlUrl))
                {
                    _logger.LogWarning("Dependency {Dep} not found in Thunderstore", dep);
                    continue;
                }
                var depResult = await InstallFromUrlAsync(def, dlUrl, depName, ct);
                if (!depResult.Ok) _logger.LogWarning("Dependency {Dep} failed: {Error}", dep, depResult.Error);
            }

            return new ModInstallResult(true, info, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Valheim mod install failed for {Url}", url);
            return new ModInstallResult(false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    private async Task<(bool Ok, string? Error, ServerDef Def)> EnsureBepInExAsync(ServerDef def, CancellationToken ct)
    {
        if (BepInExReady(def)) return (true, null, def);

        await _bootstrapGate.WaitAsync(ct);
        try
        {
            if (BepInExReady(def)) return (true, null, def);

            var pkg = await _thunderstore.FindPackageAsync("valheim", BepInExOwner, BepInExPackage, ct);
            var latest = pkg?.Versions?.FirstOrDefault();
            if (latest is null || string.IsNullOrWhiteSpace(latest.DownloadUrl))
                return (false, "Could not resolve the current BepInExPack_Valheim from Thunderstore.", def);

            var tmpRoot = Path.Combine(Path.GetTempPath(), "gsc-bepinex-" + Guid.NewGuid().ToString("n")[..8]);
            var zipPath = Path.Combine(tmpRoot, "bepinex.zip");
            var extractPath = Path.Combine(tmpRoot, "extract");
            Directory.CreateDirectory(tmpRoot);
            try
            {
                using (var resp = await _http.GetAsync(latest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!resp.IsSuccessStatusCode)
                        return (false, $"BepInEx download returned HTTP {(int)resp.StatusCode}.", def);
                    await using var fs = File.Create(zipPath);
                    await resp.Content.CopyToAsync(fs, ct);
                }
                ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

                var packRoot = Directory.EnumerateDirectories(extractPath, BepInExPackage, SearchOption.AllDirectories).FirstOrDefault();
                if (packRoot is null)
                    return (false, "BepInEx archive did not contain BepInExPack_Valheim.", def);

                CopyDirectory(packRoot, def.GuestWorkingDir);
                Directory.CreateDirectory(ModsFolder(def));

                if (!OperatingSystem.IsWindows())
                {
                    var script = Path.Combine(def.GuestWorkingDir, "start_server_bepinex.sh");
                    await File.WriteAllTextAsync(script, LinuxBepInExLauncher(), ct);
                    try
                    {
                        var mode = File.GetUnixFileMode(script);
                        File.SetUnixFileMode(script, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not chmod {Script}", script); }

                    def = _store.Update(def.Id, def with { GuestExePath = script });
                }

                _logger.LogInformation("Installed BepInExPack_Valheim {Version} for {ServerId}", latest.VersionNumber, def.Id);
                return BepInExReady(def)
                    ? (true, null, def)
                    : (false, "BepInEx files were copied but the loader could not be verified.", def);
            }
            finally
            {
                try { Directory.Delete(tmpRoot, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BepInEx bootstrap failed for {ServerId}", def.Id);
            return (false, ex.Message, def);
        }
        finally { _bootstrapGate.Release(); }
    }

    private static bool BepInExReady(ServerDef def)
    {
        var core = Path.Combine(def.GuestWorkingDir, "BepInEx", "core");
        if (!Directory.Exists(core) || !Directory.EnumerateFiles(core, "BepInEx*.dll", SearchOption.TopDirectoryOnly).Any()) return false;
        if (OperatingSystem.IsWindows()) return File.Exists(Path.Combine(def.GuestWorkingDir, "winhttp.dll"));
        return File.Exists(Path.Combine(def.GuestWorkingDir, "doorstop_libs", "libdoorstop_x64.so")) &&
               File.Exists(Path.Combine(def.GuestWorkingDir, "start_server_bepinex.sh"));
    }

    private static string LinuxBepInExLauncher() => """
#!/bin/sh
set -e
cd "$(dirname "$0")"
export DOORSTOP_ENABLE=TRUE
export DOORSTOP_INVOKE_DLL_PATH=./BepInEx/core/BepInEx.Preloader.dll
export LD_LIBRARY_PATH="./doorstop_libs:${LD_LIBRARY_PATH:-}"
export LD_PRELOAD="libdoorstop_x64.so:${LD_PRELOAD:-}"
export SteamAppId=892970
exec ./valheim_server.x86_64 "$@"
""";

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        if (!Supports(def)) return Task.FromResult(false);
        var modsDir = ModsFolder(def);
        if (!Directory.Exists(modsDir)) return Task.FromResult(false);

        var meta = LoadMetadata(def);
        var ok = false;
        if (meta.TryGetValue(modId, out var sidecar) && sidecar.Files is { Length: > 0 } files)
        {
            foreach (var rel in files)
            {
                var abs = Path.Combine(def.GuestWorkingDir, rel);
                try { if (File.Exists(abs)) { File.Delete(abs); ok = true; } } catch { }
            }
            var modFolder = Path.Combine(modsDir, modId);
            if (Directory.Exists(modFolder) && !Directory.EnumerateFileSystemEntries(modFolder).Any())
                try { Directory.Delete(modFolder); } catch { }
            UpdateMetadata(def, m => m.Remove(modId));
            return Task.FromResult(ok);
        }

        var path = Path.Combine(modsDir, modId);
        if (File.Exists(path)) try { File.Delete(path); ok = true; } catch { }
        if (Directory.Exists(path)) try { Directory.Delete(path, recursive: true); ok = true; } catch { }
        return Task.FromResult(ok);
    }

    public async Task<ModUpdateInfo[]> CheckUpdatesAsync(ServerDef def, CancellationToken ct)
    {
        if (!Supports(def)) return Array.Empty<ModUpdateInfo>();
        var installed = await ListAsync(def, ct);
        var updates = new List<ModUpdateInfo>();
        foreach (var mod in installed)
        {
            if (string.IsNullOrWhiteSpace(mod.Source)) continue;
            var parsed = ThunderstoreClient.ParseDownloadUrl(mod.Source);
            if (parsed is null) continue;
            var pkg = await _thunderstore.FindPackageAsync("valheim", parsed.Value.owner, parsed.Value.name, ct);
            var latest = pkg?.Versions?.FirstOrDefault();
            if (latest?.VersionNumber is null || latest.DownloadUrl is null) continue;
            if (!string.Equals(latest.VersionNumber, mod.Version, StringComparison.OrdinalIgnoreCase))
                updates.Add(new ModUpdateInfo(mod.ModId, mod.DisplayName, mod.Version, latest.VersionNumber, latest.DownloadUrl));
        }
        return updates.ToArray();
    }

    private static (string? folder, string[]? loose) LocateModPayload(string extractRoot)
    {
        var plugins = Directory.EnumerateDirectories(extractRoot, "plugins", SearchOption.AllDirectories)
            .FirstOrDefault(p => Directory.EnumerateFiles(p, "*.dll", SearchOption.AllDirectories).Any());
        if (plugins is not null) return (plugins, null);

        var topDlls = Directory.GetFiles(extractRoot, "*.dll", SearchOption.TopDirectoryOnly);
        if (topDlls.Length > 0) return (null, topDlls);

        var subfolders = Directory.GetDirectories(extractRoot);
        if (subfolders.Length == 1 && Directory.EnumerateFiles(subfolders[0], "*.dll", SearchOption.AllDirectories).Any())
            return (subfolders[0], null);
        return (null, null);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "mod" : clean;
    }

    private sealed record SidecarEntry(string? DisplayName, string? Version, string? Source, DateTimeOffset? InstalledAt, string[]? Files);
    private static SidecarEntry ToSidecar(ModInfo info) => new(info.DisplayName, info.Version, info.Source, info.InstalledAt, info.Files);

    private Dictionary<string, SidecarEntry> LoadMetadata(ServerDef def)
    {
        var path = MetadataPath(def);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SidecarEntry>>(File.ReadAllText(path));
            return dict is null ? new(StringComparer.OrdinalIgnoreCase) : new(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mod sidecar read failed at {Path}", path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void UpdateMetadata(ServerDef def, Action<Dictionary<string, SidecarEntry>> mutate)
    {
        var path = MetadataPath(def);
        var dict = LoadMetadata(def);
        mutate(dict);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Mod sidecar write failed at {Path}", path); }
    }
}
