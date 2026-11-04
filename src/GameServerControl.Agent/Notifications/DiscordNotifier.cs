using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Notifications;

/// <summary>
/// Posts lifecycle events to a per-server Discord webhook URL. One colored embed per event.
/// Failures are swallowed so Discord being down can't block a server start.
/// </summary>
public sealed class DiscordNotifier
{
    private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger<DiscordNotifier> _logger;

    public DiscordNotifier(ILogger<DiscordNotifier> logger)
    {
        _logger = logger;
    }

    public enum Kind { Up, Down, Warn, Info }

    public bool IsConfigured(ServerDef def) => !string.IsNullOrWhiteSpace(def.DiscordWebhookUrl);

    /// <summary>Reject arbitrary URLs to prevent the Agent from being used as an SSRF proxy.</summary>
    public static bool IsValidWebhookUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.Host is not ("discord.com" or "discordapp.com")) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "api" || parts[1] != "webhooks"
            || !parts[2].All(char.IsAsciiDigit) || parts[3].Length < 30)
            return false;
        return parts[3].All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    public async Task SendAsync(ServerDef def, Kind kind, string title, string description, CancellationToken ct = default)
    {
        if (!IsConfigured(def) || (def.DiscordAlertsOnly && kind != Kind.Warn)) return;
        if (!IsValidWebhookUrl(def.DiscordWebhookUrl))
        {
            _logger.LogWarning("Discord webhook URL rejected for server {Id}", def.Id);
            return;
        }
        var color = kind switch
        {
            Kind.Up   => 0x3FFF8E,  // green
            Kind.Down => 0xFF5C6A,  // red
            Kind.Warn => 0xFFC857,  // yellow
            _         => 0x66D9FF   // blue
        };
        var payload = new
        {
            username = "Game Server Control",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title,
                    description,
                    color,
                    footer = new { text = def.Name + " · " + def.GameType },
                    timestamp = DateTime.UtcNow.ToString("o")
                }
            }
        };
        var body = JsonSerializer.Serialize(payload);
        try
        {
            using var resp = await _http.PostAsync(def.DiscordWebhookUrl,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            // Discord returns 204 No Content on success
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Discord webhook for {Id} returned {Code}", def.Id, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            // HttpClient exceptions may include the credential-bearing webhook URL.
            _logger.LogWarning("Discord webhook delivery failed for {Id} ({ErrorType})", def.Id, ex.GetType().Name);
        }
    }

    /// <summary>Direct send to a URL — used by the "test webhook" endpoint before the URL is saved.</summary>
    public async Task SendRawAsync(string webhookUrl, string title, string description, CancellationToken ct = default)
    {
        if (!IsValidWebhookUrl(webhookUrl))
            throw new ArgumentException("A Discord HTTPS webhook URL is required.");
        var payload = new
        {
            username = "Game Server Control",
            embeds = new[]
            {
                new { title, description, color = 0x66D9FF, timestamp = DateTime.UtcNow.ToString("o") }
            }
        };
        var body = JsonSerializer.Serialize(payload);
        using var resp = await _http.PostAsync(webhookUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord webhook returned HTTP {(int)resp.StatusCode}.");
    }
}
