using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Hubs;

[Authorize]
public sealed class StatusHub : Hub
{
    private readonly ServerRegistry _registry;
    private readonly LocalProcessService _local;

    public StatusHub(ServerRegistry registry, LocalProcessService local)
    {
        _registry = registry;
        _local = local;
    }

    public async Task SubscribeConsole(string serverId)
    {
        var def = _registry.Get(serverId);
        if (def is null) throw new HubException("Unknown server.");

        var profile = GameProfiles.FindFor(def);
        var mode = profile?.Capabilities.ConsoleMode ?? GameConsoleMode.None;

        if (mode == GameConsoleMode.Stdin)
        {
            var state = _local.GetConsoleState(def);
            await Clients.Caller.SendAsync("consoleState", new
            {
                serverId,
                mode = "stdin",
                supported = state.Supported,
                inputAvailable = state.InputAvailable,
                message = state.Message
            });
            foreach (var line in state.Lines)
                await Clients.Caller.SendAsync("logLine", line);
            return;
        }

        await Clients.Caller.SendAsync("consoleState", new
        {
            serverId,
            mode = mode == GameConsoleMode.Rcon ? "rcon" : mode == GameConsoleMode.LogOnly ? "log" : "none",
            supported = mode != GameConsoleMode.None,
            inputAvailable = false,
            message = mode switch
            {
                GameConsoleMode.Rcon => "Commands are sent through the game's RCON handler.",
                GameConsoleMode.LogOnly => "This console is read-only and follows the configured server log.",
                _ => "No live console integration is configured for this game."
            }
        });
    }

    [Authorize(Policy = "admin")]
    public async Task SendConsoleInput(string serverId, string command)
    {
        var def = _registry.Get(serverId);
        if (def is null) throw new HubException("Unknown server.");

        var (ok, error) = await _local.SendConsoleInputAsync(def, command, Context.ConnectionAborted);
        if (!ok) throw new HubException(error);
    }
}
