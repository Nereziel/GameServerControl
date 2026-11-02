using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Security;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// On first startup, if Agent:ApiToken is empty or a known placeholder, generates a
/// random 32-byte token and persists an encrypted envelope to appsettings.json.
/// Existing reversible appsettings secrets are migrated to encrypted envelopes here so
/// runtime configuration is plaintext only in memory before dependent services start.
/// </summary>
public static class FirstRunTokenGenerator
{
    private static readonly string[] PlaceholderTokens =
    {
        "",
        "REPLACE_WITH_32_CHARS+",
        "CHANGE_ME",
        "your-token-here",
        "<generate>"
    };

    public static bool EnsureToken(IConfiguration cfg, string baseDir, ILogger logger, out string token)
    {
        var dataDir = cfg["Agent:DataDir"] ?? baseDir;
        var protector = new SecretProtector(dataDir);
        var appsettingsPath = Path.Combine(baseDir, "appsettings.json");
        var current = cfg["Agent:ApiToken"] ?? "";
        var generated = false;

        if (protector.IsProtected(current))
        {
            token = protector.Unprotect(current);
            cfg["Agent:ApiToken"] = token;
        }
        else if (!IsPlaceholder(current))
        {
            token = current;
        }
        else
        {
            generated = true;
            token = GenerateToken();
            try
            {
                EnsureAppSettingsExists(baseDir, appsettingsPath, token, protector, logger);

                var json = File.ReadAllText(appsettingsPath);
                var persisted = protector.Protect(token);
                var updated = Regex.Replace(
                    json,
                    "\"ApiToken\"\\s*:\\s*\"[^\"]*\"",
                    $"\"ApiToken\": \"{persisted}\"",
                    RegexOptions.Singleline);

                if (updated == json && !json.Contains("\"ApiToken\"", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Could not find ApiToken field in appsettings.json to update.");
                }
                else
                {
                    var backup = appsettingsPath + ".bak";
                    File.Copy(appsettingsPath, backup, overwrite: true);
                    SecretFilePermissions.RestrictExisting(backup);
                    SecretFilePermissions.WriteAllTextAtomic(appsettingsPath, updated);
                }

                cfg["Agent:ApiToken"] = token;
                logger.LogInformation("Generated a new agent API token. The encrypted credential was saved to the restricted appsettings.json state file.");
                Console.WriteLine();
                Console.WriteLine("====================================================================");
                Console.WriteLine("  Generated a new Agent API token and saved it encrypted at rest in:");
                Console.WriteLine("  " + appsettingsPath);
                Console.WriteLine("  The credential itself is intentionally not written to service stdout.");
                Console.WriteLine("====================================================================");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist generated token to appsettings.json");
            }
        }

        try
        {
            AppSettingsSecretProtection.ProtectAndLoad(cfg, appsettingsPath, protector);
            token = cfg["Agent:ApiToken"] ?? token;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to migrate/decrypt protected appsettings secrets");
            throw;
        }

        return generated;
    }

    private static void EnsureAppSettingsExists(string baseDir, string appsettingsPath, string token, SecretProtector protector, ILogger logger)
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
                ApiToken = protector.Protect(token),
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
