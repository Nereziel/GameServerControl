using System.Text.Json;
using GameServerControl.Agent.Servers;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Append-only JSON-lines audit log of authenticated API mutations.
///
/// One line per mutating request:
///   {"ts":"2026-05-16T13:45:21Z","ip":"100.90.15.50","method":"POST",
///    "path":"/api/servers/satisfactory-1/config","status":200}
///
/// Reads (<c>GET</c>) are skipped to keep the log small and useful — what matters
/// for audit is who CHANGED what, not who looked.
///
/// File is rotated daily by appending the date suffix; old files are kept indefinitely
/// (operator can prune). Path is configurable via <c>Agent:AuditLogPath</c>; default
/// next to the agent's own log directory.
/// </summary>
public sealed class AuditLogger
{
    private readonly string _logDir;
    private readonly object _writeLock = new();
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(IConfiguration cfg, IHostEnvironment env, ILogger<AuditLogger> logger)
    {
        _logger = logger;
        var configured = cfg["Agent:AuditLogPath"];
        _logDir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "Logs", "audit")
            : configured;
        Directory.CreateDirectory(_logDir);
    }

    public void Record(HttpContext ctx, int statusCode)
    {
        // Skip GETs — pure reads don't change state. We log POST/PUT/DELETE/PATCH.
        var method = ctx.Request.Method;
        if (method == HttpMethods.Get || method == HttpMethods.Head || method == HttpMethods.Options)
            return;

        try
        {
            var entry = new
            {
                ts = DateTimeOffset.UtcNow.ToString("O"),
                ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
                method,
                path = ctx.Request.Path.Value,
                status = statusCode,
                user = ctx.User?.Identity?.Name ?? "bearer"
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
}

/// <summary>
/// ASP.NET Core middleware that pipes every API response into the audit log.
/// Registered after Authorization so we only log authenticated requests.
///
/// The complete-server deletion endpoint is intentionally implemented here rather
/// than as a broad filesystem endpoint. RoleEnforcementMiddleware has already
/// required an Admin token before this middleware runs, and the action is audited
/// exactly like every other mutation.
/// </summary>
public sealed class AuditMiddleware
{
    private const string DeletePrefix = "/api/servers/";
    private const string DeleteSuffix = "/delete-completely";

    private readonly RequestDelegate _next;
    private readonly AuditLogger _log;
    public AuditMiddleware(RequestDelegate next, AuditLogger log) { _next = next; _log = log; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (TryGetCompleteDeleteServerId(ctx, out var id))
            await HandleCompleteDeleteAsync(ctx, id!);
        else
            await _next(ctx);

        // Only audit /api/* paths and only when the user passed auth/role checks.
        if (ctx.Request.Path.StartsWithSegments("/api"))
            _log.Record(ctx, ctx.Response.StatusCode);
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
