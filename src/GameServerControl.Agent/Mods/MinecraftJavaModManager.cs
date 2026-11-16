using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Minecraft Paper plugins and Fabric mods installed from version-pinned
/// Modrinth CDN URLs, with Minecraft-version/loader and SHA-512 verification.
/// Arbitrary user URLs are not fetched (prevents SSRF to Agent-local services).
/// </summary>
public sealed class MinecraftJavaModManager : IModManager
{
    private sealed record SidecarEntry(string DisplayName, string? Version, string? Source,
        DateTimeOffset? InstalledAt, string? ProjectId);
    private readonly HttpClient _http;
    private readonly ILogger<MinecraftJavaModManager> _logger;

    public MinecraftJavaModManager(IHttpClientFactory factory, ILogger<MinecraftJavaModManager> logger)
    {
        _http = factory.CreateClient("minecraft-modding");
        _logger = logger;
    }

    public bool Supports(ServerDef def) =>
        def.HostingMode == HostingMode.BareMetal &&
        def.GameType == GameType.Minecraft &&
        def.GameProfileKey is "minecraft-paper" or "minecraft-fabric";

    public string ModsFolder(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, def.GameProfileKey == "minecraft-paper" ? "plugins" : "mods");

    private string MetadataPath(ServerDef def) => Path.Combine(ModsFolder(def), ".gsc-java-mods.json");

    public string? MarketplaceSource(ServerDef def) => Supports(def) ? "modrinth.com" : null;

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var folder = ModsFolder(def);
        if (!Directory.Exists(folder)) return Task.FromResult(Array.Empty<ModInfo>());
        Dictionary<string, SidecarEntry> metadata;
        try { metadata = ModMetadataStore.ReadForUpdate<SidecarEntry>(MetadataPath(def)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unreadable Minecraft mod metadata for {Id}", def.Id);
            metadata = new(StringComparer.OrdinalIgnoreCase);
        }
        var mods = Directory.EnumerateFiles(folder, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
            .Select(file =>
            {
                var name = Path.GetFileName(file);
                metadata.TryGetValue(name, out var saved);
                var stat = new FileInfo(file);
                return new ModInfo(name, saved?.DisplayName ?? Path.GetFileNameWithoutExtension(name),
                    saved?.Version, saved?.Source, saved?.InstalledAt ?? stat.CreationTimeUtc,
                    stat.Length, new[] { Path.GetRelativePath(def.GuestWorkingDir, file) });
            }).OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.FromResult(mods);
    }

    public async Task<ModSearchResult[]> SearchAsync(
        ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct)
    {
        if (!Supports(def)) return Array.Empty<ModSearchResult>();
        var (version, loader) = ServerVersion(def);
        if (string.IsNullOrEmpty(version)) return Array.Empty<ModSearchResult>();
        try
        {
            var type = loader == "paper" ? "plugin" : "mod";
            var facets = JsonSerializer.Serialize(new[]
            {
                new[] { "project_type:" + type },
                new[] { "categories:" + loader }
            });
            var url = "https://api.modrinth.com/v2/search?query=" + Uri.EscapeDataString(query ?? "") +
                "&facets=" + Uri.EscapeDataString(facets) + "&limit=" + Math.Clamp(limit, 1, 15);
            var root = await _http.GetFromJsonAsync<JsonNode>(url, ct);
            if (root?["hits"] is not JsonArray hits) return Array.Empty<ModSearchResult>();
            var items = new List<ModSearchResult>();
            foreach (var project in hits)
            {
                ct.ThrowIfCancellationRequested();
                if (project is null) continue;
                if (serverSideOnly && (string?)project["server_side"] == "unsupported") continue;
                var id = (string?)project["project_id"];
                var slug = (string?)project["slug"];
                if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[A-Za-z0-9]+$")) continue;
                var versions = await GetVersionsAsync(id, version, loader, ct);
                var selected = versions.FirstOrDefault(v => PrimaryJar(v) is not null);
                if (selected is null) continue;
                var file = PrimaryJar(selected)!;
                var download = (string?)file["url"];
                if (!TryParseModrinthUrl(download, out _, out _, out _)) continue;
                items.Add(new ModSearchResult(
                    (string?)project["title"] ?? id, (string?)project["author"] ?? "",
                    (string?)selected["version_number"] ?? "",
                    (string?)project["description"], (string?)project["icon_url"], download!,
                    "https://modrinth.com/" + type + "/" + Uri.EscapeDataString(slug ?? id),
                    (long?)project["downloads"] ?? 0, 0,
                    (project["categories"]?.AsArray().Select(x => x?.ToString() ?? "").ToArray() ?? Array.Empty<string>()),
                    (string?)project["status"] == "archived",
                    (string?)project["server_side"] == "required"));
            }
            return items.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Modrinth Minecraft mod search failed");
            return Array.Empty<ModSearchResult>();
        }
    }

    public async Task<ModInstallResult> InstallFromUrlAsync(
        ServerDef def, string url, string? displayName, CancellationToken ct,
        string? expectedModId = null)
    {
        if (!Supports(def)) return new(false, null, "Unsupported Minecraft server type.");
        if (!TryParseModrinthUrl(url, out var project, out var versionId, out var fileName))
            return new(false, null, "Only version-pinned Modrinth .jar CDN downloads are supported.");
        if (!ModInstallTransaction.MatchesExpectedModId(expectedModId, fileName))
            return new(false, null, "Downloaded JAR file name differs from the installed plugin/mod.");
        var (minecraftVersion, loader) = ServerVersion(def);
        if (string.IsNullOrEmpty(minecraftVersion))
            return new(false, null, "The Minecraft server version is unknown. Use an installer-created server.");
        var folder = ModsFolder(def);
        var temp = "";
        try
        {
            var versionNode = await _http.GetFromJsonAsync<JsonNode>(
                "https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(versionId), ct);
            if ((string?)versionNode?["project_id"] != project ||
                versionNode?["game_versions"]?.AsArray().All(v => v?.ToString() != minecraftVersion) != false ||
                versionNode?["loaders"]?.AsArray().All(v => v?.ToString() != loader) != false)
                return new(false, null, "The selected mod is not compatible with this Minecraft version/loader.");
            var matched = versionNode?["files"]?.AsArray()
                .FirstOrDefault(x => (string?)x?["url"] == url);
            var checksum = (string?)matched?["hashes"]?["sha512"];
            if (matched is null || string.IsNullOrWhiteSpace(checksum))
                return new(false, null, "Modrinth did not publish an expected SHA-512 for this JAR.");
            Directory.CreateDirectory(folder);
            temp = Path.Combine(folder, ".gsc-mc-download-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                await stream.CopyToAsync(dest, ct);
            }
            await using (var input = File.OpenRead(temp))
            {
                var actual = Convert.ToHexString(await SHA512.HashDataAsync(input, ct));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    return new(false, null, "Mod download SHA-512 mismatch.");
            }
            if (!ModRemovalSafety.TryResolveModDirectory(folder, fileName, out var destination))
                return new(false, null, "Unsafe mod file name.");
            var info = new ModInfo(fileName, displayName ?? Path.GetFileNameWithoutExtension(fileName),
                (string?)versionNode?["version_number"], url, DateTimeOffset.UtcNow,
                new FileInfo(temp).Length, new[] { Path.GetRelativePath(def.GuestWorkingDir, destination) });
            using var tx = ModInstallTransaction.BeginFiles(folder, new[] { (Source: temp, Destination: destination) });
            var retained = tx.Complete(() =>
            {
                var meta = ModMetadataStore.ReadForUpdate<SidecarEntry>(MetadataPath(def));
                meta[fileName] = new SidecarEntry(info.DisplayName, info.Version, url, info.InstalledAt, project);
                ModMetadataStore.WriteAtomic(MetadataPath(def), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            });
            if (retained) _logger.LogWarning("Previous Minecraft JAR retained after replacement of {Name}", fileName);
            return new(true, info, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Minecraft Java mod installation failed for {Id}", def.Id);
            return new(false, null, "Mod install failed. Check Agent logs and verify that the server is stopped.");
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
    }

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        if (!Supports(def) || !Regex.IsMatch(modId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\\.jar$"))
            return Task.FromResult(false);
        var folder = ModsFolder(def);
        if (!ModRemovalSafety.TryResolveModDirectory(folder, modId, out var destination) ||
            !File.Exists(destination)) return Task.FromResult(false);
        var previous = destination + ".gsc-prev-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(destination, previous);
            try
            {
                var meta = ModMetadataStore.ReadForUpdate<SidecarEntry>(MetadataPath(def));
                meta.Remove(modId);
                ModMetadataStore.WriteAtomic(MetadataPath(def), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                File.Move(previous, destination);
                throw;
            }
            try { File.Delete(previous); } catch { _logger.LogWarning("Old mod file retained at {Path}", previous); }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to uninstall Minecraft mod {Id}", modId);
            return Task.FromResult(false);
        }
    }

    private static (string? Version, string Loader) ServerVersion(ServerDef def)
    {
        var file = Path.Combine(def.GuestWorkingDir, ".gsc-minecraft.json");
        try
        {
            var meta = JsonNode.Parse(File.ReadAllText(file));
            return ((string?)meta?["version"],
                def.GameProfileKey == "minecraft-paper" ? "paper" : "fabric");
        }
        catch { return (null, def.GameProfileKey == "minecraft-paper" ? "paper" : "fabric"); }
    }

    private async Task<JsonNode[]> GetVersionsAsync(string project, string minecraftVersion, string loader, CancellationToken ct)
    {
        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }));
        var versions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion }));
        var result = await _http.GetFromJsonAsync<JsonNode>(
            "https://api.modrinth.com/v2/project/" + project + "/version?loaders=" + loaders +
            "&game_versions=" + versions, ct);
        return result?.AsArray().Where(x => x is not null).Select(x => x!).ToArray() ?? Array.Empty<JsonNode>();
    }

    private static JsonNode? PrimaryJar(JsonNode version) =>
        version["files"]?.AsArray().FirstOrDefault(f =>
            f is not null && ((string?)f["filename"])?.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) == true &&
            (bool?)f["primary"] == true)
        ?? version["files"]?.AsArray().FirstOrDefault(f =>
            f is not null && ((string?)f["filename"])?.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) == true);

    public static bool TryParseModrinthUrl(
        string? downloadUrl, out string projectId, out string versionId, out string fileName)
    {
        projectId = versionId = fileName = "";
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "data" || parts[2] != "versions" ||
            !Regex.IsMatch(parts[1], "^[A-Za-z0-9]+$") ||
            !Regex.IsMatch(parts[3], "^[A-Za-z0-9]+$") ||
            !Regex.IsMatch(parts[4], "^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\\.jar$", RegexOptions.IgnoreCase))
            return false;
        projectId = parts[1];
        versionId = parts[3];
        fileName = parts[4];
        return true;
    }
}
