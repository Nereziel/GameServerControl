using GameServerControl.Agent.Notifications;

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

        return api;
    }
}
