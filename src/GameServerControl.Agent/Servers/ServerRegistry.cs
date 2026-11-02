using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Agent.Security;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class ServerRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<ServerRegistry> _logger;
    private readonly string _path;
    private readonly SecretProtector _protector;
    private readonly object _lock = new();
    private List<ServerDef> _servers = new();

    public ServerRegistry(IConfiguration cfg, IHostEnvironment env, ILogger<ServerRegistry> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:ServersJsonPath"] ?? "servers.json";
        _path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        var dataDir = cfg["Agent:DataDir"] ?? env.ContentRootPath;
        _protector = new SecretProtector(dataDir);
        Reload();
    }

    public IReadOnlyList<ServerDef> All
    {
        get { lock (_lock) return _servers.ToArray(); }
    }

    public ServerDef? Get(string id)
    {
        lock (_lock) return _servers.FirstOrDefault(s => s.Id == id);
    }

    public void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("servers.json not found at {Path}", _path);
                lock (_lock) _servers = new();
                return;
            }
            SecretFilePermissions.RestrictExisting(_path);
            var raw = ReadStoredServers(_path);
            var migratePrimary = raw.Any(s => ServerSecretStorage.NeedsProtection(s, _protector));
            if (migratePrimary)
            {
                WriteProtectedServers(_path, raw);
                _logger.LogInformation("Migrated persisted server credentials to encrypted storage in {Path}", _path);
            }
            MigrateBackupIfNeeded(_path + ".bak");

            var loaded = raw
                .Select(s => CuratedNetwork.Normalize(ServerSecretStorage.UnprotectFromStorage(s, _protector)))
                .ToList();
            lock (_lock) _servers = loaded;
            _logger.LogInformation("Loaded {Count} servers from {Path}", _servers.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {Path}", _path);
        }
    }

    private List<ServerDef> ReadStoredServers(string path)
    {
        var doc = JsonSerializer.Deserialize<ServersFile>(File.ReadAllText(path), JsonOpts);
        return doc?.Servers?.ToList() ?? new List<ServerDef>();
    }

    private void WriteProtectedServers(string path, IEnumerable<ServerDef> servers)
    {
        var protectedServers = servers.Select(s => ServerSecretStorage.ProtectExistingForMigration(s, _protector)).ToArray();
        var json = JsonSerializer.Serialize(new ServersFile { Servers = protectedServers }, JsonOpts);
        SecretFilePermissions.WriteAllTextAtomic(path, json);
    }

    private void MigrateBackupIfNeeded(string backupPath)
    {
        if (!File.Exists(backupPath)) return;
        SecretFilePermissions.RestrictExisting(backupPath);
        var backup = ReadStoredServers(backupPath);
        if (backup.Any(s => ServerSecretStorage.NeedsProtection(s, _protector)))
            WriteProtectedServers(backupPath, backup);
    }

    private sealed class ServersFile
    {
        public ServerDef[]? Servers { get; set; }
    }
}
