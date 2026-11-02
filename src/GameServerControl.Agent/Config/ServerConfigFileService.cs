using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

public sealed record ServerConfigFileInfo(
    string Path,
    string Name,
    string Group,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string Format,
    bool RestartRecommended);

public sealed record ServerConfigFileList(
    bool Supported,
    string? Reason,
    ServerConfigFileInfo[] Files);

public sealed record ServerConfigFileContent(
    string Path,
    string Content,
    string Sha256,
    string Format,
    long SizeBytes,
    DateTimeOffset ModifiedAt);

public sealed record ServerConfigFileWriteRequest(
    string Path,
    string Content,
    string? ExpectedSha256 = null);

public sealed record ServerConfigFileWriteResult(
    string Path,
    string Sha256,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    string? BackupId);

public sealed record ServerConfigFileHistoryEntry(
    string Id,
    DateTimeOffset CreatedAt,
    long SizeBytes);

public sealed record ServerConfigFileRestoreRequest(
    string Path,
    string BackupId,
    string? ExpectedSha256 = null);

public sealed class ServerConfigFileConflictException : Exception
{
    public ServerConfigFileConflictException(string message) : base(message) { }
}

/// <summary>
/// Safe, text-only configuration file access rooted inside one BareMetal game-server folder.
/// The service deliberately does not expose a general file manager: only known config roots,
/// known text extensions, existing files, and non-symlink paths are accepted.
/// </summary>
public sealed class ServerConfigFileService
{
    private sealed record RootRule(string RelativePath, string Group, bool Recursive = true);

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cfg", ".conf", ".config", ".ini", ".json", ".jsonc", ".properties",
        ".toml", ".txt", ".xml", ".yaml", ".yml"
    };

    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxDiscoveredFiles = 500;
    private const int MaxHistoryPerFile = 20;

    private readonly ServerRegistry _registry;
    private readonly ILogger<ServerConfigFileService> _logger;

    public ServerConfigFileService(ServerRegistry registry, ILogger<ServerConfigFileService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task<ServerConfigFileList> ListAsync(string serverId, CancellationToken ct = default)
    {
        var def = GetServer(serverId);
        if (def.HostingMode != HostingMode.BareMetal)
            return Task.FromResult(new ServerConfigFileList(false,
                "Config-file editing currently supports servers running on this Agent host. Hyper-V guest file access is not enabled yet.",
                Array.Empty<ServerConfigFileInfo>()));

        var root = RequireServerRoot(def);
        var rules = RulesFor(def);
        var seen = new HashSet<string>(PathComparer());
        var result = new List<ServerConfigFileInfo>();

        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var ruleRoot = ResolveRuleRoot(root, rule.RelativePath);
            if (!Directory.Exists(ruleRoot) || IsSymlink(ruleRoot)) continue;

            var option = rule.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            IEnumerable<string> candidates;
            try { candidates = Directory.EnumerateFiles(ruleRoot, "*", option); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= MaxDiscoveredFiles) break;
                if (!AllowedExtensions.Contains(Path.GetExtension(file))) continue;
                if (IsInsideHistory(root, file) || HasSymlinkComponent(root, file)) continue;

                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }
                if (!info.Exists || info.Length > MaxFileBytes) continue;

                var relative = ToRelative(root, file);
                if (!seen.Add(relative)) continue;
                result.Add(new ServerConfigFileInfo(
                    relative,
                    Path.GetFileName(file),
                    InferGroup(relative, rule.Group),
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    FormatFor(file),
                    RestartRecommended(relative)));
            }

            if (result.Count >= MaxDiscoveredFiles) break;
        }

        return Task.FromResult(new ServerConfigFileList(true, null,
            result.OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                  .ToArray()));
    }

    public async Task<ServerConfigFileContent> ReadAsync(string serverId, string relativePath, CancellationToken ct = default)
    {
        var (def, root, full) = ResolveExistingAllowedFile(serverId, relativePath);
        _ = def;
        var bytes = await ReadBoundedAsync(full, ct);
        var info = new FileInfo(full);
        return new ServerConfigFileContent(
            ToRelative(root, full),
            Decode(bytes),
            Hash(bytes),
            FormatFor(full),
            bytes.LongLength,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public async Task<ServerConfigFileWriteResult> WriteAsync(string serverId, ServerConfigFileWriteRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var (_, root, full) = ResolveExistingAllowedFile(serverId, request.Path);
        var current = await ReadBoundedAsync(full, ct);
        EnsureExpectedHash(current, request.ExpectedSha256);
        ValidateText(full, request.Content ?? string.Empty);

        var encoding = DetectEncoding(current);
        var next = Encode(request.Content ?? string.Empty, encoding);
        if (next.LongLength > MaxFileBytes)
            throw new InvalidDataException($"Config file would exceed the {MaxFileBytes / 1024} KB limit.");

        var backupId = await BackupCurrentAsync(root, full, current, ct);
        await AtomicWriteAsync(full, next, ct);
        TrimHistory(root, full);

        var info = new FileInfo(full);
        _logger.LogInformation("Updated config file {Path} for server {ServerId}", ToRelative(root, full), serverId);
        return new ServerConfigFileWriteResult(
            ToRelative(root, full),
            Hash(next),
            next.LongLength,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            backupId);
    }

    public Task<ServerConfigFileHistoryEntry[]> HistoryAsync(string serverId, string relativePath, CancellationToken ct = default)
    {
        var (_, root, full) = ResolveExistingAllowedFile(serverId, relativePath);
        ct.ThrowIfCancellationRequested();
        var dir = HistoryDirectory(root, full);
        if (!Directory.Exists(dir)) return Task.FromResult(Array.Empty<ServerConfigFileHistoryEntry>());

        var entries = Directory.EnumerateFiles(dir, "*.bak", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(x => x.CreationTimeUtc)
            .Take(MaxHistoryPerFile)
            .Select(x => new ServerConfigFileHistoryEntry(
                x.Name,
                new DateTimeOffset(x.CreationTimeUtc, TimeSpan.Zero),
                x.Length))
            .ToArray();
        return Task.FromResult(entries);
    }

    public async Task<ServerConfigFileWriteResult> RestoreAsync(string serverId, ServerConfigFileRestoreRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.BackupId) || Path.GetFileName(request.BackupId) != request.BackupId || !request.BackupId.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid backup id.", nameof(request));

        var (_, root, full) = ResolveExistingAllowedFile(serverId, request.Path);
        var current = await ReadBoundedAsync(full, ct);
        EnsureExpectedHash(current, request.ExpectedSha256);

        var historyDir = HistoryDirectory(root, full);
        var backup = Path.Combine(historyDir, request.BackupId);
        if (!File.Exists(backup) || IsSymlink(backup)) throw new FileNotFoundException("Config history entry not found.");
        var restored = await ReadBoundedAsync(backup, ct);
        ValidateText(full, Decode(restored));

        var undoId = await BackupCurrentAsync(root, full, current, ct);
        await AtomicWriteAsync(full, restored, ct);
        TrimHistory(root, full);

        var info = new FileInfo(full);
        _logger.LogInformation("Restored config file {Path} for server {ServerId} from {BackupId}", ToRelative(root, full), serverId, request.BackupId);
        return new ServerConfigFileWriteResult(
            ToRelative(root, full),
            Hash(restored),
            restored.LongLength,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            undoId);
    }

    private ServerDef GetServer(string serverId)
        => _registry.Get(serverId) ?? throw new KeyNotFoundException(serverId);

    private static string RequireServerRoot(ServerDef def)
    {
        if (def.HostingMode != HostingMode.BareMetal)
            throw new NotSupportedException("Config-file editing for Hyper-V guests is not enabled yet.");
        if (string.IsNullOrWhiteSpace(def.GuestWorkingDir))
            throw new InvalidOperationException("Server working directory is not configured.");
        var root = Path.GetFullPath(def.GuestWorkingDir);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Server working directory does not exist.");
        if (IsSymlink(root)) throw new InvalidOperationException("Server working directory may not be a symbolic link.");
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private (ServerDef Def, string Root, string FullPath) ResolveExistingAllowedFile(string serverId, string relativePath)
    {
        var def = GetServer(serverId);
        var root = RequireServerRoot(def);
        var relative = NormalizeRelative(relativePath);
        if (!AllowedExtensions.Contains(Path.GetExtension(relative)))
            throw new ArgumentException("This file type is not allowed.", nameof(relativePath));
        if (!IsAllowedByRules(def, relative))
            throw new ArgumentException("This path is outside the allowed configuration roots.", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInside(root, full);
        if (!File.Exists(full)) throw new FileNotFoundException("Config file not found.");
        if (HasSymlinkComponent(root, full)) throw new InvalidOperationException("Symbolic-link config paths are not allowed.");
        return (def, root, full);
    }

    private static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.");
        var value = path.Replace('\\', '/').Trim();
        if (value.StartsWith('/') || Path.IsPathRooted(value) || value.Contains(':'))
            throw new ArgumentException("Path must be relative to the server folder.");
        var pieces = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0 || pieces.Any(x => x is "." or ".."))
            throw new ArgumentException("Invalid relative path.");
        return string.Join('/', pieces);
    }

    private static void EnsureInside(string root, string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison)) throw new ArgumentException("Path escapes the server folder.");
    }

    private static bool IsAllowedByRules(ServerDef def, string relative)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var rule in RulesFor(def))
        {
            var rulePath = rule.RelativePath.Replace('\\', '/').Trim('/');
            if (rulePath.Length == 0)
            {
                if (!relative.Contains('/')) return true;
                continue;
            }
            if (relative.Equals(rulePath, comparison)) return true;
            if (rule.Recursive && relative.StartsWith(rulePath + "/", comparison)) return true;
        }
        return false;
    }

    private static RootRule[] RulesFor(ServerDef def)
    {
        var profile = GameProfiles.FindFor(def)?.Key ?? "custom";
        var rules = new List<RootRule> { new(".", "Server", false) };

        void Add(string path, string group, bool recursive = true)
        {
            if (!rules.Any(x => x.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                rules.Add(new RootRule(path, group, recursive));
        }

        switch (profile)
        {
            case "valheim":
                Add("BepInEx/config", "Mods / plugins");
                Add("BepInEx/plugins", "Mods / plugins");
                break;
            case "minecraft":
                Add("config", "Mods / plugins");
                Add("plugins", "Mods / plugins");
                Add("world/serverconfig", "World config");
                break;
            case "palworld":
                Add("Pal/Saved/Config", "Server");
                Add("Pal/Binaries/Win64/Mods", "Mods / plugins");
                break;
            case "satisfactory":
                Add("FactoryGame/Saved/Config", "Server");
                Add("FactoryGame/Mods", "Mods / plugins");
                break;
            case "dragonwilds":
                Add("RSDragonwilds/Saved/Config", "Server");
                break;
            case "vrising":
                Add("BepInEx/config", "Mods / plugins");
                Add("BepInEx/plugins", "Mods / plugins");
                Add("VRisingServer_Data/StreamingAssets/Settings", "Server");
                break;
            case "ark-asa":
            case "ark-se":
                Add("ShooterGame/Saved/Config", "Server");
                break;
            case "7dtd":
                Add("data", "Server", false);
                break;
            case "dst":
                Add("data/DST", "Server");
                break;
        }

        // Common mod/config conventions are safe to expose when they exist. This keeps custom
        // profiles useful without recursively scanning the whole installation tree.
        Add("config", "Config");
        Add("configs", "Config");
        Add("plugins", "Mods / plugins");
        Add("mods", "Mods / plugins");
        Add("BepInEx/config", "Mods / plugins");
        Add("BepInEx/plugins", "Mods / plugins");
        return rules.ToArray();
    }

    private static string ResolveRuleRoot(string root, string relative)
    {
        var value = relative.Replace('\\', '/').Trim('/');
        if (value is "" or ".") return root;
        var full = Path.GetFullPath(Path.Combine(root, value.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInside(root, full);
        return full;
    }

    private static string InferGroup(string relative, string fallback)
    {
        var value = relative.Replace('\\', '/');
        if (value.Contains("BepInEx/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/plugins/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/mods/", StringComparison.OrdinalIgnoreCase))
            return "Mods / plugins";
        return fallback;
    }

    private static bool RestartRecommended(string relative)
        => !relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static string FormatFor(string path)
        => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "yml" => "yaml",
            "properties" => "properties",
            "cfg" => "ini",
            "conf" => "ini",
            _ => Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
        };

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Config file not found.");
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"Config file exceeds the {MaxFileBytes / 1024} KB limit.");
        return await File.ReadAllBytesAsync(path, ct);
    }

    private static void EnsureExpectedHash(byte[] current, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        if (!Hash(current).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ServerConfigFileConflictException("The config file changed on disk after it was opened. Reload it before saving.");
    }

    private static void ValidateText(string path, string content)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".json":
                    using (JsonDocument.Parse(content)) { }
                    break;
                case ".xml":
                case ".config":
                    XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
            throw new InvalidDataException($"Invalid {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} syntax: {ex.Message}");
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false);
    }

    private static string Decode(byte[] bytes)
    {
        var encoding = DetectEncoding(bytes);
        var preamble = encoding.GetPreamble();
        var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private static byte[] Encode(string content, Encoding encoding)
    {
        var body = encoding.GetBytes(content);
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".gsc-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task<string> BackupCurrentAsync(string root, string full, byte[] bytes, CancellationToken ct)
    {
        var dir = HistoryDirectory(root, full);
        Directory.CreateDirectory(dir);
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" + Guid.NewGuid().ToString("N")[..8] + ".bak";
        await File.WriteAllBytesAsync(Path.Combine(dir, id), bytes, ct);
        return id;
    }

    private static string HistoryDirectory(string root, string full)
    {
        var relative = ToRelative(root, full);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(root, ".gsc-history", key);
    }

    private static void TrimHistory(string root, string full)
    {
        var dir = HistoryDirectory(root, full);
        if (!Directory.Exists(dir)) return;
        foreach (var item in Directory.EnumerateFiles(dir, "*.bak")
                     .Select(x => new FileInfo(x))
                     .OrderByDescending(x => x.CreationTimeUtc)
                     .Skip(MaxHistoryPerFile))
        {
            try { item.Delete(); } catch { }
        }
    }

    private static bool IsInsideHistory(string root, string full)
    {
        var history = Path.Combine(root, ".gsc-history") + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(history, comparison);
    }

    private static bool HasSymlinkComponent(string root, string full)
    {
        if (IsSymlink(full)) return true;
        var parent = Directory.GetParent(full);
        while (parent is not null)
        {
            if (PathsEqual(parent.FullName, root)) return false;
            if (IsSymlink(parent.FullName)) return true;
            parent = parent.Parent;
        }
        return true;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return true; }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ToRelative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static StringComparer PathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
