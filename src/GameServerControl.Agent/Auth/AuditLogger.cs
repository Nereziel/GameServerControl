using System.Text.Json;
using GameServerControl.Agent.Servers;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Append-only JSON-lines audit log of authenticated API mutations.
/// Reads are intentionally skipped; mutations record the authenticated Agent token identity.
/// Cloudflare remote-user headers are trusted only when they arrive through an explicitly
/// configured Agent token id, so an ordinary bearer client cannot spoof the human identity.
/// </summary>
public sealed class AuditLogger
{
    private const string GatewayHeader = "x-gameservercontrol-gateway";
    private const string RemoteUserHeader = "x-gameservercontrol-remote-user";
    private const string CloudflareGateway = "cloudflare-worker";

    private readonly string _logDir;
    private readonly object _writeLock = new();
    private readonly ILogger<AuditLogger> _logger;
    private readonly HashSet<string> _trustedGatewayTokenIds;
    private readonly int _retentionDays;

    public AuditLogger(IConfiguration cfg, IHostEnvironment env, ILogger<AuditLogger> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:AuditLogPath"];
        _logDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "Logs", "audit")
            : configured;
        Directory.CreateDirectory(_logDir);

        _trustedGatewayTokenIds = (cfg["Agent:CloudGateway:TrustedTokenIds"] ?? "")
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _retentionDays = int.TryParse(cfg["Agent:AuditRetentionDays"], out var configuredDays)
            ? Math.Clamp(configuredDays, 1, 3650)
            : 90;
        PruneOldLogs();
    }

    public void Record(HttpContext ctx, int statusCode)
    {
        var method = ctx.Request.Method;
        if (method == HttpMethods.Get || method == HttpMethods.Head || method == HttpMethods.Options)
            return;

        try
        {
            var tokenId = ctx.User?.FindFirst(TokenAuthHandler.TokenIdClaim)?.Value;
            var role = ctx.User?.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
            var requestedGateway = ctx.Request.Headers[GatewayHeader].ToString();
            var trustedGateway = !string.IsNullOrWhiteSpace(tokenId)
                && _trustedGatewayTokenIds.Contains(tokenId)
                && string.Equals(requestedGateway, CloudflareGateway, StringComparison.OrdinalIgnoreCase);
            var remoteUser = trustedGateway
                ? NormalizeRemoteUser(ctx.Request.Headers[RemoteUserHeader].ToString())
                : null;

            var entry = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                method,
                path = ctx.Request.Path.Value,
                status = statusCode,
                user = ctx.User?.Identity?.Name ?? "bearer",
                tokenId,
                role,
                gateway = trustedGateway ? CloudflareGateway : null,
                remoteUser
            };
            var line = JsonSerializer.Serialize(entry);
            var path = Path.Combine(_logDir, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_writeLock) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Audit logging must never break the request path.
            _logger.LogDebug(ex, "Audit log write failed");
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_logDir, "audit-*.jsonl", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Audit retention could not prune {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audit retention scan failed");
        }
    }

    private static string? NormalizeRemoteUser(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        normalized = normalized.Replace("\r", "", StringComparison.Ordinal)
                               .Replace("\n", "", StringComparison.Ordinal);
        return normalized.Length <= 320 ? normalized : normalized[..320];
    }
}

/// <summary>
/// ASP.NET Core middleware that pipes API mutations into the audit log. It also owns the two
/// narrowly-scoped control-plane endpoints that need middleware context: authenticated identity
/// discovery and complete server deletion. Neither path exposes a general filesystem primitive.
/// </summary>
public sealed class AuditMiddleware
{
    private const string DeletePrefix = "/api/servers/";
    private const string DeleteSuffix = "/delete-completely";
    private const string AuthMePath = "/api/auth/me";

    private readonly RequestDelegate _next;
    private readonly AuditLogger _log;
    public AuditMiddleware(RequestDelegate next, AuditLogger log) { _next = next; _log = log; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method)
            && string.Equals(ctx.Request.Path.Value, AuthMePath, StringComparison.OrdinalIgnoreCase))
        {
            await HandleAuthMeAsync(ctx);
        }
        else if (TryGetCompleteDeleteServerId(ctx, out var id))
        {
            await HandleCompleteDeleteAsync(ctx, id!);
        }
        else
        {
            await _next(ctx);
        }

        if (ctx.Request.Path.StartsWithSegments("/api"))
            _log.Record(ctx, ctx.Response.StatusCode);
    }

    private static async Task HandleAuthMeAsync(HttpContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var tokenId = ctx.User.FindFirst(TokenAuthHandler.TokenIdClaim)?.Value;
        var role = ctx.User.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
        await ctx.Response.WriteAsJsonAsync(new
        {
            id = tokenId,
            name = ctx.User.Identity?.Name,
            role
        }, ctx.RequestAborted);
    }

    private static bool TryGetCompleteDeleteServerId(HttpContext ctx, out string? id)
    {
        id = null;
        if (!HttpMethods.IsPost(ctx.Request.Method)) return false;
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith(DeletePrefix, StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(DeleteSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var encoded = path.Substring(DeletePrefix.Length, path.Length - DeletePrefix.Length - DeleteSuffix.Length).Trim('/');
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Contains('/')) return false;
        id = Uri.UnescapeDataString(encoded);
        return true;
    }

    private static async Task HandleCompleteDeleteAsync(HttpContext ctx, string id)
    {
        DeleteServerRequest? request;
        try
        {
            request = await ctx.Request.ReadFromJsonAsync<DeleteServerRequest>(cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new DeleteServerResult(false, "invalid_request", "Invalid JSON request."), ctx.RequestAborted);
            return;
        }

        if (request is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new DeleteServerResult(false, "invalid_request", "Request body is required."), ctx.RequestAborted);
            return;
        }

        var service = ActivatorUtilities.CreateInstance<ServerDeletionService>(ctx.RequestServices);
        var result = await service.DeleteCompletelyAsync(id, request, ctx.RequestAborted);
        ctx.Response.StatusCode = result.Success
            ? StatusCodes.Status200OK
            : result.Code == "not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(result, ctx.RequestAborted);
    }
}
