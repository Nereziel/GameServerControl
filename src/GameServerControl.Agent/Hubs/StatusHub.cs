using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Logs;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Hubs;

[Authorize]
public sealed class StatusHub : Hub
{
    private const string RecentBacklogItemPrefix = "gsc:log-recent:";
    private readonly ServerRegistry _registry;
    private readonly LocalProcessService _local;
    private readonly LogTailService _logTail;

    public static string ServerGroup(string serverId) => "server:" + serverId;

    public StatusHub(ServerRegistry registry, LocalProcessService local, LogTailService logTail)
    {
        _registry = registry;
        _local = local;
        _logTail = logTail;
    }

    public async Task SubscribeConsole(string serverId)
    {
        var def = _registry.Get(serverId);
        if (def is null) throw new HubException("Unknown server.");

        await Groups.AddToGroupAsync(Context.ConnectionId, ServerGroup(serverId), Context.ConnectionAborted);

        var profile = GameProfiles.FindFor(def);
        var mode = profile?.Capabilities.ConsoleMode ?? GameConsoleMode.None;

        if (mode == GameConsoleMode.Stdin)
        {
            var state = await _local.SubscribeConsoleAsync(def, Context.ConnectionId, Context.ConnectionAborted);
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

        if (mode == GameConsoleMode.LogOnly)
        {
            var backlogKey = RecentBacklogItemPrefix + serverId;
            if (!Context.Items.ContainsKey(backlogKey))
            {
                Context.Items[backlogKey] = true;
                var recent = await _logTail.GetRecentAsync(serverId, 100, Context.ConnectionAborted);
                foreach (var line in recent)
                    await Clients.Caller.SendAsync("logLine", line, Context.ConnectionAborted);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _local.UnsubscribeConsoleConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
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
