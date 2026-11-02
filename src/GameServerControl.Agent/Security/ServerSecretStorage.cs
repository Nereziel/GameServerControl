using GameServerControl.Shared;

namespace GameServerControl.Agent.Security;

public static class ServerSecretStorage
{
    public static bool NeedsProtection(ServerDef def, SecretProtector protector)
        => NeedsProtection(def.RconPassword, protector) || NeedsProtection(def.DiscordWebhookUrl, protector);

    /// <summary>Protects runtime plaintext before persistence, even when the plaintext begins with the envelope prefix.</summary>
    public static ServerDef ProtectForStorage(ServerDef def, SecretProtector protector)
        => def with
        {
            RconPassword = ProtectRuntimeOptional(def.RconPassword, protector),
            DiscordWebhookUrl = ProtectRuntimeOptional(def.DiscordWebhookUrl, protector)
        };

    /// <summary>Protects legacy storage while preserving already-valid envelope-shaped values for decryption.</summary>
    public static ServerDef ProtectExistingForMigration(ServerDef def, SecretProtector protector)
        => def with
        {
            RconPassword = ProtectExistingOptional(def.RconPassword, protector),
            DiscordWebhookUrl = ProtectExistingOptional(def.DiscordWebhookUrl, protector)
        };

    public static ServerDef UnprotectFromStorage(ServerDef def, SecretProtector protector)
        => def with
        {
            RconPassword = UnprotectOptional(def.RconPassword, protector),
            DiscordWebhookUrl = UnprotectOptional(def.DiscordWebhookUrl, protector)
        };

    private static bool NeedsProtection(string? value, SecretProtector protector)
        => !string.IsNullOrEmpty(value) && !protector.IsProtected(value);

    private static string? ProtectRuntimeOptional(string? value, SecretProtector protector)
        => string.IsNullOrEmpty(value) ? value : protector.Protect(value);

    private static string? ProtectExistingOptional(string? value, SecretProtector protector)
        => string.IsNullOrEmpty(value) || protector.IsProtected(value) ? value : protector.Protect(value);

    private static string? UnprotectOptional(string? value, SecretProtector protector)
        => string.IsNullOrEmpty(value) ? value : protector.Unprotect(value);
}
