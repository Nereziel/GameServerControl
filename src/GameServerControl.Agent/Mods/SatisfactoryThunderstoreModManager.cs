using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Server-side mod manager for Satisfactory dedicated servers.
///
/// Backed by satisfactory.thunderstore.io for browse/search. Mods are installed under
/// &lt;install&gt;\FactoryGame\Mods\, the location Satisfactory Mod Loader (SML) expects.
///
/// Real talk: most Satisfactory mods alter buildables / recipes / content, so they need to
/// be installed on every client too. The "server-side-only" filter will produce a sparse
/// list — usually admin tools, world tweaks, and a handful of utility mods that just patch
/// server behavior. The UI's "Also show client-required" toggle exposes the full catalog.
///
/// Zip layout we handle (in priority order):
///   1. Top-level "Mods/" subdirectory inside the zip → copy its contents into Mods/.
///      (Some Thunderstore packagers use this layout to mirror the install path.)
///   2. A single top-level folder containing .smod / .pak / .uplugin files → that's the mod;
///      copy the folder into Mods/.
///   3. Loose top-level .smod / .pak files → wrap them in a folder named after the package.
/// </summary>
public sealed class SatisfactoryThunderstoreModManager : IModManager
{
    private readonly ILogger<SatisfactoryThunderstoreModManager> _logger;
    private readonly HttpClient _http;
    private readonly FicsitClient _ficsit;

    public SatisfactoryThunderstoreModManager(ILogger<SatisfactoryThunderstoreModManager> logger, FicsitClient ficsit)
    {
        _logger = logger;
        _ficsit = ficsit;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl-ModInstaller/1.0");
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "1690800";

    public string ModsFolder(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "FactoryGame", "Mods");

    public string? MarketplaceSource(ServerDef def) => "ficsit.app";

    public Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct)
        => _ficsit.SearchAsync(query, limit, serverSideOnly, ct);

    private static string MetadataPath(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "FactoryGame", ".gsc-mods.json");

    // The file extensions we care about inside a Satisfactory mod zip. We use these to
    // discover which subfolder of the extracted zip is "the mod" so we can copy it cleanly.
    private static readonly string[] ModFileExtensions = { ".smod", ".pak", ".uplugin", ".dll" };

    // ---- list ----

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var dir = ModsFolder(def);
        if (!Directory.Exists(dir)) return Task.FromResult(Array.Empty<ModInfo>());

        var meta = LoadMetadata(def);
        var result = new List<ModInfo>();

        // Satisfactory mods are folders under Mods/. Each folder is one mod.
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            if (id.StartsWith(".gsc-stage-", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(".gsc-mod-prev-", StringComparison.OrdinalIgnoreCase))
                continue; // Internal mod transaction directory.
            var sidecar = meta.GetValueOrDefault(id);
            long size = 0;
            try { size = new DirectoryInfo(sub).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { /* ignore */ }
            result.Add(new ModInfo(
                ModId: id,
                DisplayName: sidecar?.DisplayName ?? id,
                Version: sidecar?.Version,
                Source: sidecar?.Source,
                InstalledAt: sidecar?.InstalledAt ?? Directory.GetCreationTimeUtc(sub),
                SizeBytes: size,
                Files: sidecar?.Files));
        }

        return Task.FromResult(result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // ---- install ----

    public async Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct, string? expectedModId = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new ModInstallResult(false, null, "URL must be an http(s) link to a zip.");

        var modsDir = ModsFolder(def);
        Directory.CreateDirectory(modsDir);

        var tmpRoot = Path.Combine(Path.GetTempPath(), "gsc-mod-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmpRoot);
        var zipPath = Path.Combine(tmpRoot, "download.zip");
        var extractPath = Path.Combine(tmpRoot, "extract");

        try
        {
            _logger.LogInformation("Downloading Satisfactory mod from {Url}", url);
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return new ModInstallResult(false, null, $"Download returned HTTP {(int)resp.StatusCode}.");
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            try { ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true); }
            catch (Exception ex) { return new ModInstallResult(false, null, "Not a valid zip: " + ex.Message); }

            // Decide the payload — see strategies in the class doc-comment.
            var (sourceDir, looseFiles, payloadName) = LocateModPayload(extractPath, displayName);
            if (sourceDir is null && looseFiles is null)
                return new ModInstallResult(false, null,
                    "No Satisfactory mod files (.smod / .pak / .uplugin) found in the zip.");

            var modId = SanitizeFolderName(payloadName);
            if (!ModInstallTransaction.MatchesExpectedModId(expectedModId, modId))
                return new ModInstallResult(false, null, "The downloaded Satisfactory package ID does not match the installed mod.");
            if (!ModRemovalSafety.TryResolveModDirectory(modsDir, modId, out var destDir))
                return new ModInstallResult(false, null, "Unsafe mod folder name or linked destination.");
            var stageDir = Path.Combine(modsDir, ".gsc-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDir);
            try
            {
                if (sourceDir is not null)
                    CopyDirectory(sourceDir, stageDir);
                else
                    foreach (var f in looseFiles!)
                        File.Copy(f, Path.Combine(stageDir, Path.GetFileName(f)), overwrite: false);

                var installedFiles = Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(stageDir, f))
                    .Select(f => Path.GetRelativePath(def.GuestWorkingDir, Path.Combine(destDir, f)))
                    .ToList();
                var installedBytes = Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f).Length).Sum();
                using var replacement = ModInstallTransaction.BeginDirectory(stageDir, destDir);

                // Extract version + nicer name from Thunderstore manifest.json if present
                string? version = null;
                string? finalName = displayName;
                var manifest = Path.Combine(extractPath, "manifest.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        var json = JsonNode.Parse(await File.ReadAllTextAsync(manifest, ct));
                        version = json?["version_number"]?.ToString();
                        finalName ??= json?["name"]?.ToString();
                    }
                    catch { /* tolerate */ }
                }
                finalName ??= modId;

                var info = new ModInfo(
                    ModId: modId,
                    DisplayName: finalName,
                    Version: version,
                    Source: url,
                    InstalledAt: DateTimeOffset.UtcNow,
                    SizeBytes: installedBytes,
                    Files: installedFiles.ToArray());
                var retained = replacement.Complete(() => UpdateMetadata(def, m => m[modId] = ToSidecar(info)));
                if (retained) _logger.LogWarning("Previous Satisfactory mod directory retained after replacement of {ModId}", modId);
                return new ModInstallResult(true, info, null);
            }
            finally
            {
                if (Directory.Exists(stageDir)) try { Directory.Delete(stageDir, recursive: true); } catch { }
            }


        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Satisfactory mod install failed for {Url}", url);
            return new ModInstallResult(false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    // ---- uninstall ----

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        var modsDir = ModsFolder(def);
        if (!Directory.Exists(modsDir)) return Task.FromResult(false);

        if (!ModRemovalSafety.TryResolveModDirectory(modsDir, modId, out var modFolder))
            return Task.FromResult(false);
        var meta = LoadMetadata(def);
        var ok = false;

        if (meta.TryGetValue(modId, out var sidecar) && sidecar.Files is { Length: > 0 } files)
        {
            foreach (var rel in files)
            {
                if (!ModRemovalSafety.TryResolveInstalledFile(def.GuestWorkingDir, modFolder, rel, out var abs))
                {
                    _logger.LogWarning("Skipping unsafe Satisfactory mod metadata path {Path}", rel);
                    continue;
                }
                try { if (File.Exists(abs)) { File.Delete(abs); ok = true; } } catch { /* ignore */ }
            }
            if (Directory.Exists(modFolder) && ModRemovalSafety.CanDeleteDirectoryTree(modFolder))
            {
                try { Directory.Delete(modFolder, recursive: true); ok = true; } catch { /* ignore */ }
            }
            UpdateMetadata(def, m => m.Remove(modId));
            return Task.FromResult(ok);
        }

        // No sidecar: only delete a direct, non-linked child of the mods directory.
        if (Directory.Exists(modFolder) && ModRemovalSafety.CanDeleteDirectoryTree(modFolder))
        {
            try { Directory.Delete(modFolder, recursive: true); ok = true; } catch { /* ignore */ }
        }
        return Task.FromResult(ok);
    }

    // ---- helpers ----

    /// <summary>Identify what to copy out of an extracted Thunderstore zip.</summary>
    private static (string? folder, string[]? loose, string payloadName) LocateModPayload(string extractRoot, string? displayHint)
    {
        // 1) Top-level "Mods/" subfolder — copy its sole child folder (or contents)
        var modsSubdir = Path.Combine(extractRoot, "Mods");
        if (Directory.Exists(modsSubdir))
        {
            var inner = Directory.GetDirectories(modsSubdir);
            if (inner.Length == 1) return (inner[0], null, Path.GetFileName(inner[0]));
            if (Directory.EnumerateFiles(modsSubdir, "*.*", SearchOption.AllDirectories).Any(IsModFile))
                return (modsSubdir, null, displayHint ?? Path.GetFileName(extractRoot.TrimEnd('\\', '/')) ?? "mod");
        }

        // 2) Single top-level folder that contains mod files
        var topFolders = Directory.GetDirectories(extractRoot);
        if (topFolders.Length == 1 && Directory.EnumerateFiles(topFolders[0], "*", SearchOption.AllDirectories).Any(IsModFile))
            return (topFolders[0], null, Path.GetFileName(topFolders[0]));

        // 3) Top-level loose mod files
        var loose = Directory.GetFiles(extractRoot, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(IsModFile).ToArray();
        if (loose.Length > 0)
            return (null, loose, displayHint ?? Path.GetFileNameWithoutExtension(loose[0]) ?? "mod");

        return (null, null, displayHint ?? "mod");
    }

    private static bool IsModFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ModFileExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f);
            var dst = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) || clean is "." or ".." ? "mod" : clean;
    }

    // ---- metadata sidecar (same layout as Valheim, different file path) ----

    private sealed record SidecarEntry(string? DisplayName, string? Version, string? Source, DateTimeOffset? InstalledAt, string[]? Files);

    private static SidecarEntry ToSidecar(ModInfo info) =>
        new(info.DisplayName, info.Version, info.Source, info.InstalledAt, info.Files);

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
        var dict = ModMetadataStore.ReadForUpdate<SidecarEntry>(path);
        mutate(dict);
        // A failed sidecar write must trigger file rollback, not silently succeed.
        ModMetadataStore.WriteAtomic(path,
            JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }
}
