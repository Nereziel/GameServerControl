using System.Security.Cryptography;
using System.Text;
using GameServerControl.Agent.Security;

namespace GameServerControl.Agent.Config;

/// <summary>
/// DPAPI/AES-GCM protected recovery copy of the Dragonwilds AdminPassword.
/// Some game builds rewrite DedicatedServer.ini when starting and remove that
/// key. The Agent must keep the last explicitly saved password so a later
/// Start can restore it while the game process is stopped.
/// Never send this file or the cleartext password to telemetry or logs.
/// </summary>
public sealed class DragonwildsPasswordStore
{
    private readonly string _dir;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();

    public DragonwildsPasswordStore(IConfiguration config, IHostEnvironment env)
        : this(ResolveDataRoot(config, env)) { }

    public DragonwildsPasswordStore(string dataRoot)
    {
        var fullRoot = Path.GetFullPath(dataRoot);
        _dir = Path.Combine(fullRoot, "dragonwilds-passwords");
        _protector = new SecretProtector(fullRoot);
    }

    private static string ResolveDataRoot(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Agent:DataDir"] ?? env.ContentRootPath;
        return Path.IsPathRooted(configured)
            ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private string PathFor(string serverId)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId.ToLowerInvariant())));
        return Path.Combine(_dir, hex + ".secret");
    }

    public string? Load(string serverId)
    {
        lock (_gate)
        {
            var path = PathFor(serverId);
            if (!File.Exists(path)) return null;
            SecretFilePermissions.RestrictExisting(path);
            var envelope = File.ReadAllText(path);
            if (!_protector.IsProtected(envelope))
                throw new CryptographicException("Dragonwilds password recovery file is not encrypted.");
            return _protector.Unprotect(envelope);
        }
    }

    public void Remove(string serverId)
    {
        lock (_gate)
        {
            var path = PathFor(serverId);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
    }

    public void Save(string serverId, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Admin password must not be empty.", nameof(password));
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(serverId);
            SecretFilePermissions.WriteAllTextAtomic(path, _protector.Protect(password), path + ".bak");
        }
    }
}
