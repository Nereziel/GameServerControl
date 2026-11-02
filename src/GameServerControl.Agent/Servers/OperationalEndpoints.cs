using GameServerControl.Agent.Notifications;
using GameServerControl.Agent.Rcon;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

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

        return api;
    }
}
