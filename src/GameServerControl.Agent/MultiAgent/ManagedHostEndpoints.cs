using GameServerControl.Agent.Auth;

namespace GameServerControl.Agent.MultiAgent;

public static class ManagedHostEndpoints
{
    public static RouteGroupBuilder MapManagedHosts(this RouteGroupBuilder api)
    {
        // These responses intentionally never contain a remote URL or credential.
        api.MapGet("/hosts", (ManagedHostStore hosts) => Results.Ok(
            new[] { new ManagedHostView("local", Environment.MachineName, true) }.Concat(hosts.List())));

        api.MapGet("/hosts/overview", async (HttpContext ctx, ManagedHostGateway gateway,
            CancellationToken ct) => Results.Ok(await gateway.OverviewAsync(ctx.User, ct)));

        api.MapPost("/hosts", async (ManagedHostInput input, ManagedHostStore hosts,
            ManagedHostGateway gateway, CancellationToken ct) =>
        {
            try
            {
                ManagedHostStore.ValidateOrigin(input.Origin);
                await gateway.CheckCredentialsAsync(input, ct);
                return Results.Ok(hosts.Add(input));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.BadRequest(new { error = "Cannot reach the remote Agent. Verify its URL and certificates." });
            }
        }).RequireAuthorization("admin");

        api.MapDelete("/hosts/{id}", (string id, ManagedHostStore hosts) =>
            id == "local" ? Results.BadRequest(new { error = "Local host cannot be removed." })
            : hosts.Delete(id) ? Results.NoContent() : Results.NotFound()).RequireAuthorization("admin");

        // No unconstrained proxy route: only /api/* and /hubs/status are
        // forwarded. Nested managed-host routes are blocked at the gateway.
        api.MapMethods("/hosts/{id}/api/{**path}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" },
            async (string id, string path, HttpContext ctx, ManagedHostGateway gateway) =>
                await gateway.ProxyApiAsync(ctx, id, path));

        api.MapMethods("/hosts/{id}/hubs/status", new[] { "GET" },
            async (string id, HttpContext ctx, ManagedHostGateway gateway) =>
                await gateway.ProxyHubAsync(ctx, id, false));

        api.MapPost("/hosts/{id}/hubs/status/negotiate",
            async (string id, HttpContext ctx, ManagedHostGateway gateway) =>
                await gateway.ProxyHubAsync(ctx, id, true));

        return api;
    }
}
