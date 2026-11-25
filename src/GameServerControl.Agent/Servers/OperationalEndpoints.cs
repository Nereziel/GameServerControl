using GameServerControl.Agent.Notifications;
using GameServerControl.Agent.Networking;
using GameServerControl.Agent.Rcon;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed record DiscordNotificationUpdate(string? WebhookUrl = null, bool? AlertsOnly = null, bool Disable = false);

public static class OperationalEndpoints
{
    public static RouteGroupBuilder MapOperationalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/update/preflight", (AgentUpdateService updater, UpdateChecker checker) =>
        {
            var preflight = updater.GetPreflight();
            var version = checker.Latest;
            return Results.Ok(new
            {
                preflight.Supported,
                preflight.Ready,
                preflight.Platform,
                preflight.HelperPresent,
                preflight.Message,
                currentVersion = version.CurrentVersion,
                latestVersion = version.LatestVersion,
                latestUrl = version.LatestUrl,
                updateAvailable = version.UpdateAvailable,
                checkedAt = version.CheckedAt
            });
        });

        api.MapGet("/servers/{id}/admin/capabilities", (string id, ServerRegistry reg, RconService rcon) =>
        {
            var def = reg.Get(id);
            if (def is null) return Results.NotFound();
            var commands = rcon.GetCapabilities(def);
            return Results.Ok(new
            {
                supported = commands.Length > 0,
                configured = def.RconPort is > 0 && !string.IsNullOrWhiteSpace(def.RconPassword),
                commands
            });
        });

        api.MapGet("/servers/{id}/history", (string id, int? limit, ServerRegistry reg, StatusTracker tracker) =>
        {
            if (reg.Get(id) is null) return Results.NotFound();
            return Results.Ok(new
            {
                serverId = id,
                maxSamples = StatusTracker.MaxHistoryPerServer,
                samples = tracker.History(id, limit ?? 120)
            });
        });

        // This is a passive local socket snapshot and a health request to the configured
        // Relay API. It never connects to game ports or probes public addresses.
        api.MapGet("/servers/{id}/network/diagnostics", async (
            string id, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, CancellationToken ct) =>
        {
            var def = reg.Get(id);
            if (def is null) return Results.NotFound();
            var sockets = ServerNetworkDiagnostics.Check(def);
            var relayStatus = await relay.GetStatusAsync(id, ct);
            return Results.Ok(new
            {
                processState = tracker.Get(id).ProcessState,
                sockets,
                relay = new
                {
                    relayStatus.Configured,
                    relayStatus.Enabled,
                    relayStatus.Reachable
                },
                note = "Socket checks are passive. Firewall, public forwarding and game handshake are not verified."
            });
        });

        // Only bounded generic event summaries are persisted; operation state is session-only.
        api.MapGet("/servers/{id}/diagnostics", (string id, ServerRegistry reg,
            StatusTracker tracker, ServerDiagnostics diagnostics) =>
        {
            if (reg.Get(id) is null) return Results.NotFound();
            return Results.Ok(new {
                status = tracker.Get(id),
                diagnostics = diagnostics.Snapshot(id),
                note = "Recent generic events survive an Agent restart. In-progress operation state and detailed logs are session-only."
            });
        });

        // Configured URL is never included in these settings responses.
        api.MapGet("/servers/{id}/notifications", (string id, ServerRegistry reg) =>
        {
            var def = reg.Get(id);
            return def is null ? Results.NotFound()
                : Results.Ok(new { configured = !string.IsNullOrWhiteSpace(def.DiscordWebhookUrl),
                    alertsOnly = def.DiscordAlertsOnly });
        });

        api.MapPut("/servers/{id}/notifications", (string id, DiscordNotificationUpdate input,
            ServerRegistry reg, ServerStore store) =>
        {
            try
            {
                var updated = store.SetNotifications(id, input.WebhookUrl, input.AlertsOnly, input.Disable);
                return Results.Ok(new { configured = !string.IsNullOrWhiteSpace(updated.DiscordWebhookUrl),
                    alertsOnly = updated.DiscordAlertsOnly });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("admin");

        api.MapPost("/servers/{id}/notifications/test", async (string id,
            ServerRegistry reg, DiscordNotifier discord, CancellationToken ct) =>
        {
            var def = reg.Get(id);
            if (def is null) return Results.NotFound();
            if (!DiscordNotifier.IsValidWebhookUrl(def.DiscordWebhookUrl))
                return Results.BadRequest(new { error = "Configure a valid Discord webhook first." });
            try
            {
                await discord.SendRawAsync(def.DiscordWebhookUrl!, "Webhook test",
                    "GameServerControl can send alerts for this server.", ct);
                return Results.Ok(new { ok = true });
            }
            catch (Exception)
            {
                return Results.Json(new { error = "Webhook delivery failed. Verify its configuration." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireAuthorization("admin");

        return api;
    }
}
