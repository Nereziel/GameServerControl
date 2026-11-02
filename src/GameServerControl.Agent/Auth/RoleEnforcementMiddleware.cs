using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Rejects state-changing API requests from ReadOnly tokens. A narrowly scoped POST used only to
/// start/stop the caller's live-log subscription is treated as read control-plane traffic.
/// SignalR negotiation is outside /api and SendConsoleInput is independently Admin-only on the hub.
/// </summary>
public sealed class RoleEnforcementMiddleware
{
    private const string ServerPrefix = "/api/servers/";
    private const string LogTailSuffix = "/logs/tail";
    private readonly RequestDelegate _next;

    public RoleEnforcementMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        var isWrite = ctx.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH";
        if (!isWrite || !p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || IsReadOnlyControlPost(ctx))
        {
            await _next(ctx);
            return;
        }

        var roleClaim = ctx.User?.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
        if (!string.Equals(roleClaim, nameof(TokenRole.Admin), StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("This token is read-only.");
            return;
        }
        await _next(ctx);
    }

    private static bool IsReadOnlyControlPost(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) return false;
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(LogTailSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var idLength = path.Length - ServerPrefix.Length - LogTailSuffix.Length;
        if (idLength <= 0) return false;
        var serverId = path.Substring(ServerPrefix.Length, idLength).Trim('/');
        return serverId.Length > 0 && !serverId.Contains('/');
    }
}
