using GameServerControl.Agent.Networking;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

public static class AgentSettingsEndpoints
{
    public static RouteGroupBuilder MapAgentSettings(this RouteGroupBuilder api)
    {
        api.MapGet("/settings/public-relay", async (PublicRelayManager relay, CancellationToken ct) =>
            Results.Ok(await relay.GetGlobalStatusAsync(ct)));

        api.MapPut("/settings/public-relay", async (
            PublicRelayGlobalSettings settings,
            PublicRelayManager relay,
            CancellationToken ct) =>
        {
            try { return Results.Ok(await relay.SaveGlobalAsync(settings, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("admin");

        return api;
    }
}
