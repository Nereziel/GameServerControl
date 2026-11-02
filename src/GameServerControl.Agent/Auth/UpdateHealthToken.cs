using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GameServerControl.Agent.Auth;

public static class UpdateHealthToken
{
    public const string FileName = ".update-health-token";
    private const int MaxTokenLength = 512;

    public static string CurrentVersion { get; } = typeof(UpdateHealthToken).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateHealthToken).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static bool IsValid(string baseDirectory, string presentedToken)
    {
        if (string.IsNullOrEmpty(baseDirectory) || string.IsNullOrEmpty(presentedToken)) return false;
        if (presentedToken.Length > MaxTokenLength) return false;

        try
        {
            var path = Path.Combine(baseDirectory, FileName);
            if (!File.Exists(path)) return false;

            var stored = File.ReadAllText(path);
            if (string.IsNullOrEmpty(stored) || stored.Length > MaxTokenLength) return false;

            var presentedBytes = Encoding.UTF8.GetBytes(presentedToken);
            var storedBytes = Encoding.UTF8.GetBytes(stored);
            return presentedBytes.Length == storedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(presentedBytes, storedBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
