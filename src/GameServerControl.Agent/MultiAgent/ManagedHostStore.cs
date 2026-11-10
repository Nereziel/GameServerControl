using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Security;
using GameServerControl.Shared;

namespace GameServerControl.Agent.MultiAgent;

public sealed record ManagedHostInput(
    string Id, string Name, string Origin, string AdminToken, string ReadOnlyToken);
public sealed record ManagedHostView(string Id, string Name, bool IsLocal);
internal sealed record StoredHost(
    string Id, string Name, string Origin, string AdminToken, string ReadOnlyToken);
internal sealed record HostCredentials(
    string Id, string Name, Uri Origin, string AdminToken, string ReadOnlyToken);

/// <summary>
/// Only the coordinating Agent stores remote credentials, protected by the same
/// DPAPI/AES-GCM secret-protection mechanism as existing Agent secrets.
/// No host credentials or origins are returned to the browser.
/// </summary>
public sealed class ManagedHostStore
{
    private const int MaxHosts = 20;
    private static readonly Regex HostIdPattern = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly SecretProtector _protector;
    private readonly List<StoredHost> _hosts;

    public ManagedHostStore(IConfiguration config, IHostEnvironment environment)
    {
        var dataDir = config["Agent:DataDir"] ?? environment.ContentRootPath;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "managed-hosts.json");
        _protector = new SecretProtector(dataDir);
        SecretFilePermissions.RestrictExisting(_path);
        // Never silently discard a corrupted credential store.
        _hosts = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<StoredHost>>(File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidDataException("Invalid managed-hosts.json.")
            : new List<StoredHost>();
        if (_hosts.Any(x => !HostIdPattern.IsMatch(x.Id) || !_protector.IsProtected(x.AdminToken)
            || !_protector.IsProtected(x.ReadOnlyToken)))
            throw new InvalidDataException("Managed hosts contain invalid IDs or unprotected credentials.");
    }

    public IReadOnlyList<ManagedHostView> List()
    {
        lock (_gate)
            return _hosts.Select(x => new ManagedHostView(x.Id, x.Name, false)).ToArray();
    }

    internal HostCredentials? Get(string id)
    {
        lock (_gate)
        {
            var host = _hosts.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            return host is null ? null : new HostCredentials(host.Id, host.Name, new Uri(host.Origin),
                _protector.Unprotect(host.AdminToken), _protector.Unprotect(host.ReadOnlyToken));
        }
    }

    public ManagedHostView Add(ManagedHostInput input)
    {
        var host = Normalize(input);
        if (string.IsNullOrWhiteSpace(input.AdminToken) || string.IsNullOrWhiteSpace(input.ReadOnlyToken))
            throw new ArgumentException("Both Admin and ReadOnly Agent tokens are required.");
        if (input.AdminToken == input.ReadOnlyToken)
            throw new ArgumentException("Admin and ReadOnly tokens must be different.");
        lock (_gate)
        {
            if (_hosts.Count >= MaxHosts) throw new InvalidOperationException("Maximum number of managed hosts reached.");
            if (_hosts.Any(x => x.Id == host.Id)) throw new InvalidOperationException("Host ID is already registered.");
            _hosts.Add(new StoredHost(host.Id, host.Name, host.Origin,
                _protector.Protect(input.AdminToken.Trim()), _protector.Protect(input.ReadOnlyToken.Trim())));
            try { Persist(); }
            catch { _hosts.RemoveAt(_hosts.Count - 1); throw; }
            return new ManagedHostView(host.Id, host.Name, false);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var index = _hosts.FindIndex(x => x.Id == id);
            if (index < 0) return false;
            var removed = _hosts[index];
            _hosts.RemoveAt(index);
            try { Persist(); }
            catch { _hosts.Insert(index, removed); throw; }
            return true;
        }
    }

    public static void ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new ArgumentException("A valid Agent origin URL is required.");
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Only HTTPS or Tailscale HTTP origins are allowed.");
        if (uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Provide the Agent URL only, with no path, query, credentials or fragment.");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A remote Agent cannot use localhost.");
        if (IPAddress.TryParse(uri.Host, out var address))
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal
                || address.IsIPv6Multicast || address.Equals(IPAddress.Broadcast)
                || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && (address.GetAddressBytes()[0] == 0
                        || address.GetAddressBytes()[0] >= 224
                        || address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254)))
                throw new ArgumentException("Loopback, link-local, multicast and unspecified addresses are not allowed.");
            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length != 4 || bytes[0] != 100 || bytes[1] < 64 || bytes[1] > 127)
                    throw new ArgumentException("Unencrypted HTTP is only permitted to a Tailscale-range IPv4 address (100.64.0.0/10).");
            }
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Use trusted HTTPS for named hosts; plain HTTP is limited to Tailscale IPv4.");
    }

    private static (string Id, string Name, string Origin) Normalize(ManagedHostInput input)
    {
        var id = input.Id?.Trim() ?? "";
        var name = input.Name?.Trim() ?? "";
        if (!HostIdPattern.IsMatch(id) || id is "all" or "local")
            throw new ArgumentException("Host ID must use lowercase letters, digits and hyphens, without reserved names.");
        if (name.Length is < 1 or > 80)
            throw new ArgumentException("Host name must contain 1–80 characters.");
        if (string.IsNullOrWhiteSpace(input.Origin) || input.Origin.Length > 512)
            throw new ArgumentException("Invalid Agent URL length.");
        ValidateOrigin(input.Origin.Trim());
        if (input.AdminToken?.Length is < 16 or > 1024 || input.ReadOnlyToken?.Length is < 16 or > 1024)
            throw new ArgumentException("Agent tokens must contain 16–1024 characters.");
        return (id, name, new Uri(input.Origin.Trim()).GetLeftPart(UriPartial.Authority).TrimEnd('/'));
    }

    private void Persist()
    {
        // Existing protected values are reused verbatim; never persist plaintext.
        SecretFilePermissions.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_hosts, JsonOptions),
            _path + ".bak");
    }
}
