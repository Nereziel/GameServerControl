using System.Security.Cryptography;
using System.Text.Json;
using GameServerControl.Agent.Security;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// On first startup, if Agent:ApiToken is empty or a known placeholder, generates a
/// random 32-byte token and persists it only through the encrypted configuration secret store.
/// </summary>
public static class FirstRunTokenGenerator
{
    private static readonly string[] PlaceholderTokens =
    {
        "",
        "REPLACE_WITH_32_CHARS+",
        "CHANGE_ME",
        "your-token-here",
        "<generate>",
        ConfigurationSecretStore.Sentinel
    };

    public static bool EnsureToken(
        IConfiguration cfg,
        string baseDir,
        ConfigurationSecretStore secretStore,
        ILogger logger,
        out string token)
    {
        var current = cfg["Agent:ApiToken"] ?? "";
        if (!IsPlaceholder(current))
        {
            token = current;
            return false;
        }

        token = GenerateToken();
        var appsettingsPath = Path.Combine(baseDir, "appsettings.json");

        try
        {
            EnsureAppSettingsExists(baseDir, appsettingsPath, logger);
            secretStore.SetSecret(cfg, "Agent:ApiToken", token);

            logger.LogInformation("Generated a new agent API token. The credential was saved to encrypted local secret state.");
            Console.WriteLine();
            Console.WriteLine("====================================================================");
            Console.WriteLine("  Generated a new Agent API token and saved it to encrypted state:");
            Console.WriteLine("  " + secretStore.StatePath);
            Console.WriteLine("  The credential itself is intentionally not written to service stdout.");
            Console.WriteLine("====================================================================");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist generated token to encrypted secret state");
            throw;
        }
        return true;
    }

    private static void EnsureAppSettingsExists(string baseDir, string appsettingsPath, ILogger logger)
    {
        if (File.Exists(appsettingsPath))
        {
            SecretFilePermissions.RestrictExisting(appsettingsPath);
            return;
        }

        var examplePath = Path.Combine(baseDir, "appsettings.json.example");
        if (File.Exists(examplePath))
        {
            File.Copy(examplePath, appsettingsPath, overwrite: false);
            SecretFilePermissions.RestrictExisting(appsettingsPath);
            logger.LogInformation("Created appsettings.json from appsettings.json.example.");
            return;
        }

        var fallback = new
        {
            Logging = new
            {
                LogLevel = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning",
                    ["GameServerControl"] = "Information"
                }
            },
            Agent = new
            {
                Bind = "http://127.0.0.1:5099",
                BindHttps = "",
                CertPassword = "",
                ApiToken = "<generate>",
                Repo = "Nereziel/GameServerControl",
                ServersJsonPath = "servers.json",
                AuditLogPath = "",
                GuestCredentials = new Dictionary<string, object>()
            }
        };
        SecretFilePermissions.WriteAllTextAtomic(
            appsettingsPath,
            JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true }));
        logger.LogWarning("appsettings.json.example was missing; created a minimal appsettings.json instead.");
    }

    private static bool IsPlaceholder(string s)
    {
        if (s.Length < 16) return true;
        foreach (var p in PlaceholderTokens)
            if (string.Equals(s, p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
