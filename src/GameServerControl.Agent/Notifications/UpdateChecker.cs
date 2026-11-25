using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Notifications;

/// <summary>
/// Polls the GitHub Releases API once a day and exposes the latest known version. The
/// dashboards check /api/version on connect and show an "update available" banner if
/// the cached latest is newer than the running InformationalVersion.
/// </summary>
public sealed class UpdateChecker : IHostedService, IDisposable
{
    private static readonly HttpClient _http = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly ILogger<UpdateChecker> _logger;
    private readonly string _repo;
    private readonly string _runningVersion;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public string CurrentVersion => _runningVersion;
    public UpdateStatus Latest { get; private set; }

    public UpdateChecker(IConfiguration cfg, ILogger<UpdateChecker> logger)
    {
        _logger = logger;
        _repo = cfg["Agent:Repo"] ?? "Nereziel/GameServerControl";
        _runningVersion = typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        Latest = new UpdateStatus(
            CurrentVersion: _runningVersion,
            LatestVersion: null,
            LatestUrl: null,
            UpdateAvailable: false,
            CheckedAt: null);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"gsc-update-checker/{_runningVersion}");
    }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts is not null) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Capture before scheduling; Dispose may run before the loop starts.
            var token = _cts.Token;
            _loop = Task.Run(() => PollLoop(token));
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? loop;
        lock (_lifecycleGate)
        {
            // The instance is registered as both a singleton and a hosted
            // service. Shutdown and disposal must be safe in either order.
            _cts?.Cancel();
            loop = _loop;
        }
        if (loop is not null) try { await loop.WaitAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        try { await Task.Delay(InitialDelay, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await CheckAsync(ct);
            try { await Task.Delay(PollInterval, ct); } catch { return; }
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("GitHub Releases API returned {Code} (rate limited? unauthenticated cap is 60/hr).", (int)resp.StatusCode);
                return Latest;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            if (release?.TagName is null) return Latest;

            var latestVersion = NormalizeTag(release.TagName);
            var newer = CompareVersions(latestVersion, _runningVersion) > 0;
            Latest = new UpdateStatus(
                CurrentVersion: _runningVersion,
                LatestVersion: latestVersion,
                LatestUrl: release.HtmlUrl,
                UpdateAvailable: newer,
                CheckedAt: DateTimeOffset.UtcNow);
            _logger.LogInformation("Update check: running {Cur}, latest {Lat} (update {Av})",
                _runningVersion, latestVersion, newer ? "AVAILABLE" : "not needed");
            return Latest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed (non-fatal).");
            return Latest;
        }
    }

    private static string NormalizeTag(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;

    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var n = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < n; i++)
        {
            var aHasNum = i < aParts.Length && int.TryParse(aParts[i], out var ai);
            var bHasNum = i < bParts.Length && int.TryParse(bParts[i], out var bi);
            if (!aHasNum || !bHasNum) return string.Compare(a, b, StringComparison.Ordinal);
            ai = aHasNum ? int.Parse(aParts[i]) : 0;
            bi = bHasNum ? int.Parse(bParts[i]) : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            var source = _cts;
            _cts = null;
            if (source is null) return;
            try { source.Cancel(); }
            finally { source.Dispose(); }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}
