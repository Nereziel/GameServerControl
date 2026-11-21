using System.Collections.Concurrent;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Bounded, in-memory SteamCMD console. Never persists raw output or secrets.
/// Only the authenticated Admin API can read this buffer.
/// </summary>
public sealed class ServerUpdateConsole
{
    private const int MaxLines = 240;
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly Queue<ServerUpdateLine> Lines = new();
        public string Status = "Idle";
        public string Message = "No update has been run in this Agent session.";
        public int? Percent;
        public long Sequence;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Begin(string id, string reason)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
        {
            state.Lines.Clear();
            state.Status = "Running";
            state.Percent = null;
            state.Message = reason;
            Append(state, reason, null);
        }
    }

    public void Write(string id, string text, int? percent = null)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
        {
            if (state.Status != "Running") return;
            Append(state, text, percent);
        }
    }

    public void End(string id, bool success, string message)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
        {
            state.Status = success ? "Success" : "Failed";
            state.Message = success ? "Game update completed." : "Game update failed; inspect the update output.";
            Append(state, message, null);
        }
    }

    private static void Append(State state, string text, int? percent)
    {
        // A bounded display buffer prevents a noisy SteamCMD process from
        // accumulating unlimited memory in the Agent.
        var clean = new string(text.Where(c => c is '\t' or '\n' or '\r' || !char.IsControl(c)).ToArray());
        if (clean.Length > 1400) clean = clean[..1400] + "…";
        state.Lines.Enqueue(new ServerUpdateLine(DateTimeOffset.UtcNow, ++state.Sequence, clean));
        while (state.Lines.Count > MaxLines) state.Lines.Dequeue();
        if (percent is not null) state.Percent = Math.Clamp(percent.Value, 0, 100);
    }

    public ServerUpdateSnapshot Snapshot(string id, long? since = null)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
            return new ServerUpdateSnapshot(state.Status, state.Message, state.Percent, state.Sequence,
                state.Lines.Where(l => l.Sequence > (since ?? 0)).ToArray());
    }
}

public sealed record ServerUpdateLine(DateTimeOffset At, long Sequence, string Text);
public sealed record ServerUpdateSnapshot(string Status, string Message, int? Percent, long LatestSequence, IReadOnlyList<ServerUpdateLine> Lines);
