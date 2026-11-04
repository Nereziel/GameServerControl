using GameServerControl.Agent.Notifications;
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

        // Diagnostics are read-only and hold only a bounded in-memory event history.
        api.MapGet("/servers/{id}/diagnostics", (string id, ServerRegistry reg,
            StatusTracker tracker, ServerDiagnostics diagnostics) =>
        {
            if (reg.Get(id) is null) return Results.NotFound();
            return Results.Ok(new {
                status = tracker.Get(id),
                diagnostics = diagnostics.Snapshot(id),
                note = "Event history is kept in memory and clears when the Agent restarts."
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
            var def = reg.Get(id);
            if (def is null) return Results.NotFound();
            var url = input.Disable ? null : string.IsNullOrWhiteSpace(input.WebhookUrl)
                ? def.DiscordWebhookUrl : input.WebhookUrl.Trim();
            if (!string.IsNullOrWhiteSpace(url) && !DiscordNotifier.IsValidWebhookUrl(url))
                return Results.BadRequest(new { error = "Only valid Discord HTTPS webhook URLs are allowed." });
            var updated = store.Update(id, def with {
                DiscordWebhookUrl = url,
                DiscordAlertsOnly = input.AlertsOnly ?? def.DiscordAlertsOnly
            });
            return Results.Ok(new { configured = !string.IsNullOrWhiteSpace(updated.DiscordWebhookUrl),
                alertsOnly = updated.DiscordAlertsOnly });
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
