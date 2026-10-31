using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Read-only marketplace client used by BepInEx mod browsing.
/// Thunderstore is always available. CurseForge is an optional second catalog when
/// Agent:CurseForge:ApiKey and Agent:CurseForge:Games:{community} are configured.
/// </summary>
public sealed class ThunderstoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ThunderstoreClient> _logger;
    private readonly IConfiguration _cfg;
    private readonly Dictionary<string, CachedCatalog> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public ThunderstoreClient(ILogger<ThunderstoreClient> logger, IConfiguration cfg)
    {
        _logger = logger;
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.0 (mod-search)");
        _http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ModSearchResult[]> SearchAsync(string community, string query, int limit, bool serverSideOnly, CancellationToken ct)
    {
        var max = Math.Clamp(limit, 1, 200);
        var thunderstore = await SearchThunderstoreAsync(community, query, max, serverSideOnly, ct);

        // CurseForge does not expose a reliable generic "server-side only" flag for all games.
        // Be conservative: only include it after the user disables the server-side-only filter.
        ModSearchResult[] curseForge = Array.Empty<ModSearchResult>();
        if (!serverSideOnly)
            curseForge = await SearchCurseForgeAsync(community, query, Math.Min(max, 50), ct);

        return thunderstore
            .Concat(curseForge)
            .OrderByDescending(x => x.Downloads)
            .Take(max)
            .ToArray();
    }

    private async Task<ModSearchResult[]> SearchThunderstoreAsync(
        string community, string query, int limit, bool serverSideOnly, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(community, ct);
        if (catalog is null) return Array.Empty<ModSearchResult>();

        var q = (query ?? "").Trim();
        IEnumerable<ThunderstorePackage> matches = catalog;
        if (q.Length > 0)
        {
            matches = catalog.Where(p =>
                Contains(p.Name, q) ||
                Contains(p.Owner, q) ||
                Contains(p.Versions?.FirstOrDefault()?.Description, q) ||
                (p.Categories ?? Array.Empty<string>()).Any(c => Contains(c, q)));
        }

        if (serverSideOnly)
            matches = matches.Where(IsServerSideOnly);

        return matches
            .OrderByDescending(p => p.Versions?.FirstOrDefault()?.Downloads ?? 0)
            .Take(limit)
            .Select(ToResult)
            .ToArray();
    }

    /// <summary>
    /// Optional CurseForge catalog. A third-party API key is intentionally read only from
    /// server-side configuration and never sent to the browser.
    /// Configuration example:
    /// Agent:CurseForge:ApiKey = "..."
    /// Agent:CurseForge:Games:valheim = "1234"
    /// </summary>
    private async Task<ModSearchResult[]> SearchCurseForgeAsync(
        string community, string query, int limit, CancellationToken ct)
    {
        var apiKey = _cfg["Agent:CurseForge:ApiKey"];
        var gameIdText = _cfg[$"Agent:CurseForge:Games:{community}"];
        if (string.IsNullOrWhiteSpace(apiKey) || !int.TryParse(gameIdText, out var gameId) || gameId <= 0)
            return Array.Empty<ModSearchResult>();

        try
        {
            var url = "https://api.curseforge.com/v1/mods/search" +
                      $"?gameId={gameId}&pageSize={Math.Clamp(limit, 1, 50)}";
            if (!string.IsNullOrWhiteSpace(query))
                url += "&searchFilter=" + Uri.EscapeDataString(query.Trim());

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", apiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("CurseForge search HTTP {Code} for community {Community}",
                    (int)resp.StatusCode, community);
                return Array.Empty<ModSearchResult>();
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<CurseForgeSearchResponse>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

            return (payload?.Data ?? Array.Empty<CurseForgeMod>())
                .Select(ToCurseForgeResult)
                .Where(x => !string.IsNullOrWhiteSpace(x.DownloadUrl))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CurseForge search failed for community {Community}", community);
            return Array.Empty<ModSearchResult>();
        }
    }

    private static ModSearchResult ToCurseForgeResult(CurseForgeMod m)
    {
        var latest = m.LatestFiles?
            .Where(f => f.IsAvailable && !string.IsNullOrWhiteSpace(f.DownloadUrl))
            .OrderByDescending(f => f.FileDate)
            .FirstOrDefault();

        var categories = (m.Categories ?? Array.Empty<CurseForgeCategory>())
            .Select(c => c.Name ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Prepend("CurseForge")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ModSearchResult(
            Name: m.Name ?? m.Slug ?? "CurseForge mod",
            Owner: m.Authors?.FirstOrDefault()?.Name ?? "",
            Version: latest?.DisplayName ?? latest?.FileName ?? "?",
            Description: m.Summary,
            IconUrl: m.Logo?.ThumbnailUrl,
            DownloadUrl: latest?.DownloadUrl ?? "",
            PackageUrl: m.Links?.WebsiteUrl ?? "",
            Downloads: m.DownloadCount,
            RatingScore: m.ThumbsUpCount,
            Categories: categories,
            Deprecated: !m.IsAvailable,
            ServerSideOnly: false);
    }

    private static bool IsServerSideOnly(ThunderstorePackage p)
    {
        var cats = p.Categories ?? Array.Empty<string>();
        foreach (var c in cats)
        {
            var n = c.Replace("-", "").Replace("_", "").Replace(" ", "")
                     .ToLowerInvariant();
            if (n.Contains("serverside")) return true;
        }
        return false;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static ModSearchResult ToResult(ThunderstorePackage p)
    {
        var v = p.Versions?.FirstOrDefault();
        var cats = (p.Categories ?? Array.Empty<string>())
            .Prepend("Thunderstore")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ModSearchResult(
            Name: p.Name ?? "",
            Owner: p.Owner ?? "",
            Version: v?.VersionNumber ?? "?",
            Description: v?.Description,
            IconUrl: v?.Icon,
            DownloadUrl: v?.DownloadUrl ?? "",
            PackageUrl: p.PackageUrl ?? "",
            Downloads: v?.Downloads ?? 0,
            RatingScore: p.RatingScore,
            Categories: cats,
            Deprecated: p.IsDeprecated,
            ServerSideOnly: IsServerSideOnly(p));
    }

    public async Task<ThunderstorePackage?> FindPackageAsync(string community, string owner, string name, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(community, ct);
        if (catalog is null) return null;
        return catalog.FirstOrDefault(p =>
            string.Equals(p.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static (string owner, string name)? ParseDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = new Uri(url, UriKind.RelativeOrAbsolute);
        if (!u.IsAbsoluteUri || !u.Host.Contains("thunderstore.io", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        var ix = Array.IndexOf(parts, "download");
        if (ix < 0 || ix + 2 >= parts.Length) return null;
        return (parts[ix + 1], parts[ix + 2]);
    }

    private async Task<ThunderstorePackage[]?> GetCatalogAsync(string community, CancellationToken ct)
    {
        if (_cache.TryGetValue(community, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return cached.Packages;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(community, out cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
                return cached.Packages;

            var url = $"https://{community}.thunderstore.io/api/v1/package/";
            _logger.LogInformation("Fetching Thunderstore catalog: {Url}", url);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thunderstore catalog HTTP {Code} for {Url}", (int)resp.StatusCode, url);
                return cached?.Packages;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var packages = await JsonSerializer.DeserializeAsync<ThunderstorePackage[]>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            packages ??= Array.Empty<ThunderstorePackage>();
            _cache[community] = new CachedCatalog(packages, DateTime.UtcNow);
            _logger.LogInformation("Cached {Count} Thunderstore packages for {Community}", packages.Length, community);
            return packages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thunderstore catalog fetch failed for {Community}", community);
            return _cache.TryGetValue(community, out var stale) ? stale.Packages : null;
        }
        finally { _gate.Release(); }
    }

    private sealed record CachedCatalog(ThunderstorePackage[] Packages, DateTime FetchedAt);

    public sealed class ThunderstorePackage
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("owner")] public string? Owner { get; set; }
        [JsonPropertyName("package_url")] public string? PackageUrl { get; set; }
        [JsonPropertyName("rating_score")] public int RatingScore { get; set; }
        [JsonPropertyName("is_pinned")] public bool IsPinned { get; set; }
        [JsonPropertyName("is_deprecated")] public bool IsDeprecated { get; set; }
        [JsonPropertyName("categories")] public string[]? Categories { get; set; }
        [JsonPropertyName("versions")] public ThunderstoreVersion[]? Versions { get; set; }
    }

    public sealed class ThunderstoreVersion
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
    }

    private sealed class CurseForgeSearchResponse
    {
        [JsonPropertyName("data")] public CurseForgeMod[]? Data { get; set; }
    }

    private sealed class CurseForgeMod
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
        [JsonPropertyName("isAvailable")] public bool IsAvailable { get; set; }
        [JsonPropertyName("thumbsUpCount")] public int ThumbsUpCount { get; set; }
        [JsonPropertyName("authors")] public CurseForgeAuthor[]? Authors { get; set; }
        [JsonPropertyName("categories")] public CurseForgeCategory[]? Categories { get; set; }
        [JsonPropertyName("latestFiles")] public CurseForgeFile[]? LatestFiles { get; set; }
        [JsonPropertyName("links")] public CurseForgeLinks? Links { get; set; }
        [JsonPropertyName("logo")] public CurseForgeLogo? Logo { get; set; }
    }

    private sealed class CurseForgeAuthor
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class CurseForgeCategory
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class CurseForgeFile
    {
        [JsonPropertyName("isAvailable")] public bool IsAvailable { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
        [JsonPropertyName("fileDate")] public DateTimeOffset FileDate { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    }

    private sealed class CurseForgeLinks
    {
        [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
    }

    private sealed class CurseForgeLogo
    {
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
    }
}
