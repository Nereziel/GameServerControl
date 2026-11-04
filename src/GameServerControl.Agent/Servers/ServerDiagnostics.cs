using System.Collections.Concurrent;
using GameServerControl.Agent.Notifications;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed record DiagnosticEvent(DateTimeOffset At, string Kind, string Severity, string Message);
public sealed record DiagnosticOperation(DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string State, string Message);
public sealed record DiagnosticSnapshot(
    DateTimeOffset? LastUnexpectedStopAt,
    DateTimeOffset? LastRecoveryObservedAt,
    DateTimeOffset? LastManualRestartAt,
    IReadOnlyDictionary<string, DiagnosticOperation> Operations,
    IReadOnlyList<DiagnosticEvent> Events);

/// <summary>
/// Bounded, in-memory operational diagnostics. A process restart clears this history.
/// It never stores passwords, tokens, webhook URLs or command output.
/// </summary>
public sealed class ServerDiagnostics
{
    private const int MaxEvents = 60;
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly Queue<DiagnosticEvent> Events = new();
        public readonly Dictionary<string, DiagnosticOperation> Operations = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? LastUnexpectedStopAt;
        public DateTimeOffset? LastRecoveryObservedAt;
        public DateTimeOffset? LastManualRestartAt;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string id, string kind, string severity, string message)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            state.Events.Enqueue(new DiagnosticEvent(now, kind, severity, message));
            while (state.Events.Count > MaxEvents) state.Events.Dequeue();
            if (kind == "UnexpectedStop") state.LastUnexpectedStopAt = now;
            if (kind == "RecoveryObserved") state.LastRecoveryObservedAt = now;
            if (kind == "ManualRestart") state.LastManualRestartAt = now;
        }
    }

    public DiagnosticSnapshot Snapshot(string id)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
            return new DiagnosticSnapshot(
                state.LastUnexpectedStopAt,
                state.LastRecoveryObservedAt,
                state.LastManualRestartAt,
                new Dictionary<string, DiagnosticOperation>(state.Operations),
                state.Events.Reverse().ToArray());
    }

    private void Begin(string id, string operation)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        lock (state.Gate)
            state.Operations[operation] = new DiagnosticOperation(DateTimeOffset.UtcNow, null, "Running", "In progress");
        Record(id, operation, "Info", operation + " started");
    }

    private void Finish(string id, string operation, bool success, string message)
    {
        var state = _states.GetOrAdd(id, _ => new State());
        // Result messages from game tools can contain paths, credentials or remote output.
        // Diagnostics use a safe, generic result; details remain in the existing action response.
        var safe = success ? "Completed successfully" : "Failed; review the Agent log or action response";
        lock (state.Gate)
        {
            var start = state.Operations.TryGetValue(operation, out var old)
                ? old.StartedAt : DateTimeOffset.UtcNow;
            state.Operations[operation] = new DiagnosticOperation(start, DateTimeOffset.UtcNow,
                success ? "Success" : "Failed", safe);
        }
        Record(id, operation, success ? "Success" : "Error", operation + ": " + safe);
    }

    public async Task<ActionResult> TrackAsync(
        string id, string operation, Func<Task<ActionResult>> run,
        ServerDef? server, DiscordNotifier discord, CancellationToken ct = default)
    {
        Begin(id, operation);
        try
        {
            var result = await run();
            Finish(id, operation, result.Success, result.Message);
            if (server is not null && discord.IsConfigured(server))
            {
                if (!result.Success)
                    _ = discord.SendAsync(server, DiscordNotifier.Kind.Warn,
                        operation + " failed", "Check the GameServerControl Diagnostics tab for this server.", CancellationToken.None);
                else if (operation is "Backup" or "Update")
                    _ = discord.SendAsync(server, DiscordNotifier.Kind.Info,
                        operation + " completed", "Operation completed successfully.", CancellationToken.None);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(id, operation, false, "Cancelled");
            throw;
        }
        catch (Exception)
        {
            Finish(id, operation, false, "Unexpected error");
            if (server is not null && discord.IsConfigured(server))
                _ = discord.SendAsync(server, DiscordNotifier.Kind.Warn,
                    operation + " failed", "Unexpected error; check the Agent logs.", CancellationToken.None);
            throw;
        }
    }
}
