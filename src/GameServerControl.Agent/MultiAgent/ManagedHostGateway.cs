using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.MultiAgent;

public sealed record HostServerSummary(
    string Id, string Name, string GameType, string HostingMode,
    string ProcessState, double? CpuPercent, long? MemoryMB,
    int? PidInGuest, DateTimeOffset? LastBackupAt, bool HasError);

public sealed record ManagedHostSummary(
    string Id, string Name, bool IsLocal, bool Online,
    string? Error, IReadOnlyList<HostServerSummary> Servers);

/// <summary>
/// The central Agent forwards authenticated requests to explicitly registered
/// remote Agents. The browser never receives remote bearer tokens.
/// </summary>
public sealed class ManagedHostGateway
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(7)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly ManagedHostStore _hosts;
    private readonly ServerRegistry _registry;
    private readonly StatusTracker _statuses;

    public ManagedHostGateway(ManagedHostStore hosts, ServerRegistry registry, StatusTracker statuses)
    {
        _hosts = hosts;
        _registry = registry;
        _statuses = statuses;
    }

    public async Task CheckCredentialsAsync(ManagedHostInput registration, CancellationToken ct)
    {
        ManagedHostStore.ValidateOrigin(registration.Origin);
        var origin = new Uri(registration.Origin.Trim().TrimEnd('/') + "/");
        // Distinct credentials must actually have the claimed upstream roles;
        // otherwise ReadOnly callers could receive remote Admin privileges.
        await CheckRoleAsync(origin, registration.AdminToken, "Admin", ct);
        await CheckRoleAsync(origin, registration.ReadOnlyToken, "ReadOnly", ct);
    }

    private static async Task CheckRoleAsync(Uri origin, string token, string expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "api/auth/me"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Remote Agent authentication failed. Check network reachability and the managed token.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);
        if (!json.RootElement.TryGetProperty("role", out var role) ||
            !string.Equals(role.GetString(), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The remote token must have the " + expected + " role.");
    }

    public async Task<IReadOnlyList<ManagedHostSummary>> OverviewAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var local = _registry.All.Select(def =>
        {
            var status = _statuses.Get(def.Id);
            return new HostServerSummary(def.Id, def.Name, def.GameType.ToString(),
                def.HostingMode.ToString(), status.ProcessState.ToString(),
                status.CpuPercent, status.MemoryMB, status.PidInGuest, status.LastBackupAt,
                !string.IsNullOrEmpty(status.LastError));
        }).ToArray();

        var response = new List<ManagedHostSummary>
        {
            new("local", Environment.MachineName, true, true, null, local)
        };
        // Only the ReadOnly credentials are used to build the combined dashboard.
        // Limit fanout to registered hosts and return individual offline states.
        var remotes = _hosts.List();
        var snapshots = await Task.WhenAll(remotes.Select(async host =>
        {
            try { return await SnapshotRemoteAsync(host.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return new ManagedHostSummary(host.Id, host.Name, false, false,
                    "Host unreachable or authentication failed.", Array.Empty<HostServerSummary>());
            }
        }));
        response.AddRange(snapshots);
        return response;
    }

    private async Task<ManagedHostSummary> SnapshotRemoteAsync(string id, CancellationToken ct)
    {
        var host = _hosts.Get(id) ?? throw new KeyNotFoundException(id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var defs = await ReadJsonAsync(host, "api/servers", host.ReadOnlyToken, timeout.Token);
        var statuses = await ReadJsonAsync(host, "api/status", host.ReadOnlyToken, timeout.Token);
        if (defs.RootElement.ValueKind != JsonValueKind.Array || statuses.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unexpected remote Agent response.");
        using (defs)
        using (statuses)
        {
            var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var status in statuses.RootElement.EnumerateArray())
            {
                var serverId = ReadString(status, "id");
                if (!string.IsNullOrEmpty(serverId)) byId[serverId] = status;
            }

            var servers = new List<HostServerSummary>();
            foreach (var def in defs.RootElement.EnumerateArray().Take(200))
            {
                var serverId = ReadString(def, "id");
                if (string.IsNullOrWhiteSpace(serverId)) continue;
                byId.TryGetValue(serverId, out var status);
                servers.Add(new HostServerSummary(
                    serverId, ReadString(def, "name"), ReadString(def, "gameType"),
                    ReadString(def, "hostingMode"),
                    ReadString(status, "processState", "Unknown"),
                    ReadDouble(status, "cpuPercent"), ReadLong(status, "memoryMB"),
                    ReadInt(status, "pidInGuest"), ReadDate(status, "lastBackupAt"),
                    !string.IsNullOrEmpty(ReadString(status, "lastError"))));
            }
            return new ManagedHostSummary(host.Id, host.Name, false, true, null, servers);
        }
    }

    private static string ReadString(JsonElement element, string key, string fallback = "")
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind != JsonValueKind.Null ? value.ToString() : fallback;

    private static double? ReadDouble(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
           ? number : null;

    private static long? ReadLong(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
           ? number : null;

    private static int? ReadInt(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
           ? number : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string key)
        => DateTimeOffset.TryParse(ReadString(element, key), out var at) ? at : null;

    private static async Task<JsonDocument> ReadJsonAsync(
        HostCredentials host, string path, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(host.Origin, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024)
            throw new InvalidOperationException("Remote Agent overview unavailable.");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
    }

    private HostCredentials? Select(string id) => _hosts.Get(id);

    private static string TokenFor(HostCredentials host, ClaimsPrincipal user)
    {
        var admin = string.Equals(user.FindFirst(TokenAuthHandler.RoleClaim)?.Value,
            "Admin", StringComparison.OrdinalIgnoreCase);
        return admin ? host.AdminToken : host.ReadOnlyToken;
    }

    private static Uri RouteUri(HostCredentials host, string route, IQueryCollection query)
    {
        if (route.StartsWith('/') || route.Contains('\\') || route.Contains("//", StringComparison.Ordinal)
            || route.Split('/').Any(part => part is "." or ".." || part.IndexOfAny(['?', '#', '\0']) >= 0)
            || route.Length > 2048)
            throw new ArgumentException("Invalid remote route.");
        // Compose only beneath the pre-validated remote origin, never from a
        // caller-supplied host, scheme, port or absolute URL.
        var queryString = string.Join("&", query.Where(x => !x.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Select(value => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(value ?? ""))));
        var url = new Uri(host.Origin, route);
        var builder = new UriBuilder(url) { Query = queryString };
        if (builder.Host != host.Origin.Host || builder.Port != host.Origin.Port ||
            builder.Scheme != host.Origin.Scheme)
            throw new ArgumentException("Invalid remote route.");
        return builder.Uri;
    }

    public async Task ProxyApiAsync(HttpContext context, string hostId, string path)
    {
        var host = Select(hostId);
        if (host is null) { context.Response.StatusCode = 404; return; }
        if (string.IsNullOrWhiteSpace(path) || path.Split('/')[0].Equals("hosts", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            return;
        }
        await ProxyHttpAsync(context, host, "api/" + path);
    }

    public async Task ProxyHubAsync(HttpContext context, string hostId, bool negotiate)
    {
        var host = Select(hostId);
        if (host is null) { context.Response.StatusCode = 404; return; }
        if (context.WebSockets.IsWebSocketRequest)
        {
            if (negotiate) { context.Response.StatusCode = 400; return; }
            await ProxyWebSocketAsync(context, host);
            return;
        }
        if (!negotiate)
        {
            context.Response.StatusCode = 400;
            return;
        }
        await ProxyHttpAsync(context, host, "hubs/status/negotiate");
    }

    private static async Task ProxyHttpAsync(HttpContext context, HostCredentials host, string route)
    {
        if (context.Request.ContentLength > 32 * 1024 * 1024)
        {
            context.Response.StatusCode = 413;
            return;
        }

        try
        {
            var uri = RouteUri(host, route, context.Request.Query);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(host, context.User));
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                || HttpMethods.IsPatch(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
                if (!string.IsNullOrEmpty(context.Request.ContentType))
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }

            using var upstream = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            context.Response.StatusCode = (int)upstream.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            if (upstream.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            await upstream.Content.CopyToAsync(context.Response.Body, timeout.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
            or WebSocketException or ArgumentException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "Remote Agent request failed." });
            }
        }
    }

    private static async Task ProxyWebSocketAsync(HttpContext context, HostCredentials host)
    {
        try
        {
            var target = RouteUri(host, "hubs/status", context.Request.Query);
            var builder = new UriBuilder(target)
            {
                Scheme = target.Scheme == "https" ? "wss" : "ws"
            };
            using var remote = new ClientWebSocket();
            remote.Options.SetRequestHeader("Authorization", "Bearer " + TokenFor(host, context.User));
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await remote.ConnectAsync(builder.Uri, connectTimeout.Token);

            using var client = await context.WebSockets.AcceptWebSocketAsync();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var forward = PumpAsync(client, remote, lifetime.Token);
            var backward = PumpAsync(remote, client, lifetime.Token);
            await Task.WhenAny(forward, backward);
            lifetime.Cancel();
            try { await Task.WhenAll(forward, backward); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException
            or ArgumentException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new { error = "Remote Agent console unavailable." });
            }
        }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket target, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && source.State == WebSocketState.Open &&
            target.State == WebSocketState.Open)
        {
            var message = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (message.MessageType == WebSocketMessageType.Close)
            {
                await target.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                break;
            }
            await target.SendAsync(new ArraySegment<byte>(buffer, 0, message.Count),
                message.MessageType, message.EndOfMessage, ct);
        }
    }
}
