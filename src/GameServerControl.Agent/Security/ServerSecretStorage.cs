using GameServerControl.Shared;

namespace GameServerControl.Agent.Security;

public static class ServerSecretStorage
{
    public static bool NeedsProtection(ServerDef def, SecretProtector protector)
        => NeedsProtection(def.RconPassword, protector) || NeedsProtection(def.DiscordWebhookUrl, protector);

    public static ServerDef ProtectForStorage(ServerDef def, SecretProtector protector)
        => def with
        {
            RconPassword = ProtectOptional(def.RconPassword, protector),
            DiscordWebhookUrl = ProtectOptional(def.DiscordWebhookUrl, protector)
        };

    public static ServerDef UnprotectFromStorage(ServerDef def, SecretProtector protector)
        => def with
        {
            RconPassword = UnprotectOptional(def.RconPassword, protector),
            DiscordWebhookUrl = UnprotectOptional(def.DiscordWebhookUrl, protector)
        };

    private static bool NeedsProtection(string? value, SecretProtector protector)
        => !string.IsNullOrEmpty(value) && !protector.IsProtected(value);

    private static string? ProtectOptional(string? value, SecretProtector protector)
        => string.IsNullOrEmpty(value) ? value : protector.Protect(value);

    private static string? UnprotectOptional(string? value, SecretProtector protector)
        => string.IsNullOrEmpty(value) ? value : protector.Unprotect(value);
}
