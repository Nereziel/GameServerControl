using GameServerControl.Agent.Security;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Converts reusable ServerDef credentials between plaintext runtime values and
/// purpose-bound encrypted storage envelopes. The server ID is part of each purpose.
/// </summary>
public sealed class ServerSecretProtection
{
    private readonly SecretProtector _protector;

    public ServerSecretProtection(SecretProtector protector)
        => _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public ServerDef ProtectForStorage(ServerDef def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return def with
        {
            RconPassword = ProtectOptional(def.RconPassword, Purpose(def.Id, "rcon-password")),
            DiscordWebhookUrl = ProtectOptional(def.DiscordWebhookUrl, Purpose(def.Id, "discord-webhook"))
        };
    }

    public ServerDef UnprotectFromStorage(ServerDef def, out bool legacyPlaintext)
    {
        ArgumentNullException.ThrowIfNull(def);
        legacyPlaintext = false;
        return def with
        {
            RconPassword = UnprotectOptional(def.RconPassword, Purpose(def.Id, "rcon-password"), ref legacyPlaintext),
            DiscordWebhookUrl = UnprotectOptional(def.DiscordWebhookUrl, Purpose(def.Id, "discord-webhook"), ref legacyPlaintext)
        };
    }

    private string? ProtectOptional(string? value, string purpose)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return SecretProtector.IsProtected(value) ? value : _protector.Protect(value, purpose);
    }

    private string? UnprotectOptional(string? value, string purpose, ref bool legacyPlaintext)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (SecretProtector.IsProtected(value)) return _protector.Unprotect(value, purpose);
        legacyPlaintext = true;
        return value;
    }

    private static string Purpose(string serverId, string field)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new InvalidOperationException("Cannot protect a server secret without a stable server ID.");
        return $"server:{serverId}:{field}";
    }
}
