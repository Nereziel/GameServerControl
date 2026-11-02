using System.Text.Json.Nodes;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Rejects state-changing API requests from ReadOnly tokens and sanitizes sensitive GET responses.
/// A narrowly scoped POST used only to start/stop the caller's live-log subscription is treated as
/// read control-plane traffic. SignalR negotiation is outside /api and SendConsoleInput is
/// independently Admin-only on the hub.
/// </summary>
public sealed class RoleEnforcementMiddleware
{
    private const string ServerPrefix = "/api/servers/";
    private const string LogTailSuffix = "/logs/tail";
    private const string ConfigSuffix = "/config";
    private const string FileContentSuffix = "/files/content";
    private readonly RequestDelegate _next;

    public RoleEnforcementMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        var roleClaim = ctx.User?.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
        var isReadOnly = string.Equals(roleClaim, nameof(TokenRole.ReadOnly), StringComparison.OrdinalIgnoreCase);

        if (isReadOnly && HttpMethods.IsGet(ctx.Request.Method))
        {
            if (string.Equals(p, "/api/servers", StringComparison.OrdinalIgnoreCase))
            {
                var registry = ctx.RequestServices.GetRequiredService<ServerRegistry>();
                await Results.Ok(registry.All.Select(ServerViewDto.From)).ExecuteAsync(ctx);
                return;
            }

            if (IsServerEndpoint(p, FileContentSuffix))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Raw configuration file content requires an Admin token.");
                return;
            }

            if (IsServerEndpoint(p, ConfigSuffix))
            {
                await InvokeWithRedactedConfigAsync(ctx);
                return;
            }
        }

        var isWrite = ctx.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH";
        if (!isWrite || !p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || IsReadOnlyControlPost(ctx))
        {
            await _next(ctx);
            return;
        }

        if (!string.Equals(roleClaim, nameof(TokenRole.Admin), StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("This token is read-only.");
            return;
        }
        await _next(ctx);
    }

    private async Task InvokeWithRedactedConfigAsync(HttpContext ctx)
    {
        var originalBody = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await _next(ctx);
            buffer.Position = 0;

            if (ctx.Response.StatusCode is >= 200 and < 300)
            {
                using var reader = new StreamReader(buffer, leaveOpen: true);
                var json = await reader.ReadToEndAsync(ctx.RequestAborted);
                if (TryRedactPasswordValues(json, out var redacted))
                {
                    ctx.Response.Body = originalBody;
                    ctx.Response.ContentLength = null;
                    await ctx.Response.WriteAsync(redacted, ctx.RequestAborted);
                    return;
                }
            }

            buffer.Position = 0;
            ctx.Response.Body = originalBody;
            ctx.Response.ContentLength = null;
            await buffer.CopyToAsync(originalBody, ctx.RequestAborted);
        }
        finally
        {
            ctx.Response.Body = originalBody;
        }
    }

    private static bool TryRedactPasswordValues(string json, out string redacted)
    {
        redacted = json;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return false; }
        if (node is not JsonObject root || root["values"] is not JsonObject values) return false;

        var secretValues = new JsonObject();
        if (root["schema"]?["sections"] is JsonArray sections)
        {
            foreach (var section in sections)
            {
                if (section?["fields"] is not JsonArray fields) continue;
                foreach (var field in fields)
                {
                    if (!string.Equals(field?["type"]?.GetValue<string>(), nameof(ConfigFieldType.Password), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var key = field?["key"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    var hasValue = values[key] is JsonValue value
                        && value.TryGetValue<string>(out var text)
                        && !string.IsNullOrEmpty(text);
                    values.Remove(key);
                    secretValues[key] = hasValue;
                }
            }
        }

        root["secretValues"] = secretValues;
        redacted = root.ToJsonString();
        return true;
    }

    private static bool IsServerEndpoint(string path, string suffix)
    {
        if (!path.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var idLength = path.Length - ServerPrefix.Length - suffix.Length;
        if (idLength <= 0) return false;
        var serverId = path.Substring(ServerPrefix.Length, idLength).Trim('/');
        return serverId.Length > 0 && !serverId.Contains('/');
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
