using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Security;
using Microsoft.Extensions.Configuration;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Invoked only by the Windows Scheduled Tasks service as LocalSystem.
/// Loads the existing DPAPI-protected Admin credential in process memory,
/// then posts a single allowlisted operation to Agent's loopback listener.
/// </summary>
public static class ScheduledMaintenanceCommand
{
    public static async Task<int> RunAsync(string action, string serverId)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (action is not ("restart" or "update" or "backup") ||
            !Regex.IsMatch(serverId, "^[a-z][a-z0-9-]{0,39}$"))
            return 2;

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();
            var stored = configuration["Agent:ApiToken"] ?? "";
            if (!stored.StartsWith("enc:v1:dpapi:", StringComparison.Ordinal))
                return 3; // Require encrypted-at-rest token, never a plaintext fallback.
            var protector = new SecretProtector(configuration["Agent:DataDir"] ?? AppContext.BaseDirectory);
            var token = protector.Unprotect(stored);
            if (string.IsNullOrWhiteSpace(token)) return 3;

            var bind = new Uri(configuration["Agent:Bind"] ?? "http://127.0.0.1:5099");
            if (bind.Scheme != Uri.UriSchemeHttp || bind.Port is < 1 or > 65535)
                return 4; // Scheduled callbacks intentionally use loopback HTTP only.
            var requestUrl = new Uri($"http://127.0.0.1:{bind.Port}/api/servers/{serverId}/{action}");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode ? 0 : 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Scheduled maintenance request failed: " + ex.GetType().Name);
            return 6;
        }
    }
}
