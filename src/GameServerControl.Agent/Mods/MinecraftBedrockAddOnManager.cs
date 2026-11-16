using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Local Bedrock .mcpack/.mcaddon import, world activation and safe uninstall.
/// This is intentionally an upload-only workflow: arbitrary external URLs and
/// executable Bedrock script packs are never accepted by the Agent.
/// </summary>
public sealed class MinecraftBedrockAddOnManager : IModManager
{
    private sealed record Pack(string Id, string Kind, string Name, int[] Version, string Folder);
    private readonly ILogger<MinecraftBedrockAddOnManager> _logger;
    private readonly StatusTracker _tracker;

    public MinecraftBedrockAddOnManager(
        ILogger<MinecraftBedrockAddOnManager> logger, StatusTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    public bool Supports(ServerDef def) =>
        def.HostingMode == HostingMode.BareMetal &&
        def.GameType == GameType.Minecraft && def.GameProfileKey == "minecraft-bedrock";

    public string ModsFolder(ServerDef def) => Path.Combine(def.GuestWorkingDir, "behavior_packs");

    public string? MarketplaceSource(ServerDef def) => null;

    public Task<ModSearchResult[]> SearchAsync(
        ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct) =>
        Task.FromResult(Array.Empty<ModSearchResult>());

    public Task<ModInstallResult> InstallFromUrlAsync(
        ServerDef def, string url, string? displayName, CancellationToken ct, string? expectedModId = null) =>
        Task.FromResult(new ModInstallResult(false, null,
            "Bedrock add-ons must be uploaded as local .mcpack/.mcaddon files; remote URLs are not accepted."));

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var installed = new List<ModInfo>();
        foreach (var kind in new[] { "behavior", "resource" })
        {
            var baseDir = Path.Combine(def.GuestWorkingDir, kind + "_packs");
            if (!Directory.Exists(baseDir)) continue;
            foreach (var dir in Directory.EnumerateDirectories(baseDir))
            {
                try
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    var pack = ParsePack(dir);
                    if (pack.Kind != kind) continue;
                    var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(def.GuestWorkingDir, path)).ToArray();
                    installed.Add(new ModInfo(kind + "-" + pack.Id, pack.Name,
                        string.Join(".", pack.Version), null,
                        Directory.GetCreationTimeUtc(dir),
                        files.Sum(rel => new FileInfo(Path.Combine(def.GuestWorkingDir, rel)).Length),
                        files));
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Skipping unreadable Bedrock pack at {Dir}", dir); }
            }
        }
        return Task.FromResult(installed.ToArray());
    }

    public async Task<ModInstallResult> ImportAsync(
        ServerDef def, string uploadedName, Stream payload, CancellationToken ct)
    {
        if (!Supports(def)) return new(false, null, "Not an official Bedrock server.");
        if (!IsStopped(def)) return new(false, null, "Stop Bedrock before changing world add-ons.");
        var ext = Path.GetExtension(uploadedName).ToLowerInvariant();
        if (ext is not (".mcpack" or ".mcaddon"))
            return new(false, null, "Upload a .mcpack or .mcaddon file.");
        if (!Directory.Exists(def.GuestWorkingDir))
            return new(false, null, "Installed Bedrock server folder is missing.");

        string world;
        try { world = ResolveWorld(def); }
        catch (Exception ex) { return new(false, null, ex.Message); }
        var root = Path.GetFullPath(def.GuestWorkingDir);
        var staging = Path.Combine(root, ".gsc-bedrock-import-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(root, ".gsc-bedrock-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        var transactions = new List<ModInstallTransaction.IReplacement>();
        var configSnapshots = new Dictionary<string, string?>();
        var committed = false;
        try
        {
            await using (var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                await payload.CopyToAsync(dest, ct);
            Directory.CreateDirectory(staging);
            MinecraftServerPackages.ExtractSafeZip(temp, staging);
            var paths = new List<string> { staging };
            if (ext == ".mcaddon")
            {
                // Bedrock bundles typically contain nested .mcpack archives.
                var embedded = Directory.EnumerateFiles(staging, "*.mcpack", SearchOption.AllDirectories)
                    .Take(9).ToArray();
                if (embedded.Length > 8) throw new InvalidDataException("Too many packs in .mcaddon.");
                foreach (var file in embedded)
                {
                    var folder = Path.Combine(staging, ".pack-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    MinecraftServerPackages.ExtractSafeZip(file, folder);
                    paths.Add(folder);
                }
            }
            var packDirs = paths.SelectMany(path => Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)).Where(dir => dir is not null).Distinct().ToArray();
            var packs = packDirs.Select(dir => ParsePack(dir!)).GroupBy(p => p.Kind + ":" + p.Id)
                .Select(group => group.First()).ToArray();
            if (packs.Length is < 1 or > 8)
                throw new InvalidDataException("No supported Bedrock behavior/resource manifests found (maximum 8 packs).");
            if (packs.Any(p => p.Kind is not ("behavior" or "resource")))
                throw new InvalidDataException("Unsupported Bedrock pack type. Script modules are not installed.");

            foreach (var pack in packs)
            {
                ct.ThrowIfCancellationRequested();
                var kindRoot = Path.Combine(root, pack.Kind + "_packs");
                Directory.CreateDirectory(kindRoot);
                if (!ModRemovalSafety.TryResolveModDirectory(kindRoot, pack.Id, out var destDir))
                    throw new InvalidDataException("Unsafe pack directory.");
                if (Directory.Exists(destDir)) throw new InvalidOperationException("This pack is already installed: " + pack.Name);
                var stage = Path.Combine(kindRoot, ".gsc-stage-" + Guid.NewGuid().ToString("N"));
                CopyTree(pack.Folder, stage);
                transactions.Add(ModInstallTransaction.BeginDirectory(stage, destDir));
            }

            // Keep both original world config snapshots for rollback if either
            // atomic write fails. Pack transactions are still reversible here.
            foreach (var kind in new[] { "behavior", "resource" })
            {
                var matching = packs.Where(p => p.Kind == kind).ToArray();
                if (matching.Length == 0) continue;
                var target = Path.Combine(world, "world_" + kind + "_packs.json");
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked Bedrock world config is not allowed.");
                var original = File.Exists(target) ? File.ReadAllText(target) : null;
                configSnapshots[target] = original;
                var entries = original is null ? new JsonArray() :
                    JsonNode.Parse(original)?.AsArray() ?? throw new InvalidDataException("Invalid world pack configuration.");
                foreach (var pack in matching)
                    entries.Add(new JsonObject
                    {
                        ["pack_id"] = pack.Id,
                        ["version"] = new JsonArray(pack.Version.Select(v => JsonValue.Create(v)).ToArray())
                    });
                ModMetadataStore.WriteAtomic(target, entries.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            committed = true;
            foreach (var transaction in transactions)
                if (transaction.Complete())
                    _logger.LogWarning("Previous Bedrock add-on files retained after completed import");
            var first = packs[0];
            return new(true, new ModInfo(first.Kind + "-" + first.Id,
                packs.Length > 1 ? $"{packs.Length} Bedrock add-ons" : first.Name,
                string.Join(".", first.Version), null, DateTimeOffset.UtcNow, 0, null), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bedrock add-on import failed for {Id}", def.Id);
            if (!committed)
            {
                foreach (var snapshot in configSnapshots)
                {
                    try
                    {
                        if (snapshot.Value is null) File.Delete(snapshot.Key);
                        else ModMetadataStore.WriteAtomic(snapshot.Key, snapshot.Value);
                    }
                    catch (Exception restoreEx) { _logger.LogError(restoreEx, "Failed to roll back Bedrock world config {File}", snapshot.Key); }
                }
            }
            return new(false, null, "Bedrock add-on import failed: " + ex.Message);
        }
        finally
        {
            // Dispose in reverse order so uncommitted installations are restored.
            foreach (var tx in transactions.AsEnumerable().Reverse())
                try { tx.Dispose(); } catch (Exception ex) { _logger.LogError(ex, "Bedrock add-on rollback failed"); }
            if (Directory.Exists(staging)) try { Directory.Delete(staging, recursive: true); } catch { }
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
    }

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        if (!Supports(def) || !IsStopped(def)) return Task.FromResult(false);
        var match = Regex.Match(modId, "^(behavior|resource)-([a-fA-F0-9-]{36})$");
        if (!match.Success || !Guid.TryParse(match.Groups[2].Value, out var guid))
            return Task.FromResult(false);
        var kind = match.Groups[1].Value;
        var kindDir = Path.Combine(def.GuestWorkingDir, kind + "_packs");
        if (!ModRemovalSafety.TryResolveModDirectory(kindDir, guid.ToString("D"), out var installed) ||
            !Directory.Exists(installed) || !ModRemovalSafety.CanDeleteDirectoryTree(installed))
            return Task.FromResult(false);
        string world;
        try { world = ResolveWorld(def); }
        catch { return Task.FromResult(false); }
        var configPath = Path.Combine(world, "world_" + kind + "_packs.json");
        var original = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var previous = Path.Combine(kindDir, ".gsc-prev-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(configPath) && (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
                return Task.FromResult(false);
            var data = original is null ? new JsonArray() : JsonNode.Parse(original)?.AsArray()
                ?? throw new InvalidDataException("Invalid Bedrock world config.");
            var changed = data.Where(entry => (string?)entry?["pack_id"] == guid.ToString("D")).ToArray();
            foreach (var entry in changed) data.Remove(entry);
            Directory.Move(installed, previous);
            try { ModMetadataStore.WriteAtomic(configPath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch
            {
                Directory.Move(previous, installed);
                throw;
            }
            try { Directory.Delete(previous, recursive: true); }
            catch { _logger.LogWarning("Old Bedrock pack retained at {Path}", previous); }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bedrock add-on removal failed for {Id}", def.Id);
            return Task.FromResult(false);
        }
    }

    private bool IsStopped(ServerDef def) =>
        _tracker.Get(def.Id).ProcessState == ProcessState.NotRunning;

    private static string ResolveWorld(ServerDef def)
    {
        var root = Path.GetFullPath(def.GuestWorkingDir);
        var properties = Path.Combine(root, "server.properties");
        var name = File.Exists(properties)
            ? File.ReadLines(properties).Where(line => line.TrimStart().StartsWith("level-name=", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Split('=', 2)[1].Trim()).FirstOrDefault() : null;
        name = string.IsNullOrWhiteSpace(name) ? "Bedrock level" : name;
        if (name is "." or ".." || Path.GetFileName(name) != name || name.Contains(':') || name.Contains('\\'))
            throw new InvalidDataException("Unsafe Bedrock world directory name.");
        var worldsRoot = Path.Combine(root, "worlds");
        if (!ModRemovalSafety.TryResolveModDirectory(worldsRoot, name, out var world) ||
            !Directory.Exists(world))
            throw new InvalidOperationException("Bedrock world does not exist. Start the server once, stop it, and retry.");
        return world;
    }

    private static Pack ParsePack(string folder)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        var data = JsonNode.Parse(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Bedrock add-on manifest is empty.");
        var head = data["header"] ?? throw new InvalidDataException("Bedrock manifest header missing.");
        if (!Guid.TryParse((string?)head["uuid"], out var uuid))
            throw new InvalidDataException("Invalid Bedrock pack UUID.");
        var versions = head["version"]?.AsArray().Select(v => (int?)v)
            .ToArray() ?? Array.Empty<int?>();
        if (versions.Length != 3 || versions.Any(v => v is null or < 0))
            throw new InvalidDataException("Invalid Bedrock pack version.");
        var types = data["modules"]?.AsArray().Select(m => (string?)m?["type"]).ToArray() ??
            Array.Empty<string?>();
        if (types.Length == 0 || types.Any(type => type is not ("data" or "resources")))
            throw new InvalidDataException("Only data/resource Bedrock packs are accepted.");
        if (types.Distinct().Count() != 1)
            throw new InvalidDataException("Mixed Bedrock pack module types are not supported.");
        return new Pack(uuid.ToString("D"), types[0] == "data" ? "behavior" : "resource",
            (string?)head["name"] ?? uuid.ToString("D"), versions.Select(v => v!.Value).ToArray(), folder);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked add-on directory.");
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked add-on file.");
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }
}
