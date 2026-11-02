using System.Security.Cryptography;
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
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<ServerRegistry> _logger;
    private readonly string _path;
    private readonly ServerSecretProtection _secretProtection;
    private readonly object _lock = new();
    private List<ServerDef> _servers = new();

    public ServerRegistry(IConfiguration cfg, IHostEnvironment env, ILogger<ServerRegistry> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:ServersJsonPath"] ?? "servers.json";
        _path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        _secretProtection = new ServerSecretProtection(new SecretProtector(env.ContentRootPath));
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
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<ServersFile>(json, JsonOpts);
            var migratedLegacy = false;
            var loaded = new List<ServerDef>();
            foreach (var stored in doc?.Servers ?? Array.Empty<ServerDef>())
            {
                var clear = _secretProtection.UnprotectFromStorage(stored, out var legacy);
                migratedLegacy |= legacy;
                loaded.Add(CuratedNetwork.Normalize(clear));
            }

            if (migratedLegacy)
            {
                var protectedDoc = new ServersFile { Servers = loaded.Select(_secretProtection.ProtectForStorage).ToArray() };
                var protectedJson = JsonSerializer.Serialize(protectedDoc, JsonOpts);
                var backupPath = _path + ".bak";
                // Remove a legacy plaintext backup before replacing the primary so a crash
                // cannot leave the old reusable credentials behind after migration.
                if (File.Exists(backupPath)) File.Delete(backupPath);
                SecretFilePermissions.WriteAllTextAtomic(_path, protectedJson);
                SecretFilePermissions.WriteAllTextAtomic(backupPath, protectedJson);
                _logger.LogInformation("Migrated reusable server credentials to encrypted state in {Path}", _path);
            }

            lock (_lock) _servers = loaded;
            _logger.LogInformation("Loaded {Count} servers from {Path}", _servers.Count, _path);
        }
        catch (CryptographicException ex)
        {
            _logger.LogCritical(ex, "Encrypted server state could not be authenticated at {Path}", _path);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {Path}", _path);
        }
    }

    private sealed class ServersFile
    {
        public ServerDef[]? Servers { get; set; }
    }
}
