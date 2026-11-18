using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Bounded diagnostics. Safe event summaries survive an Agent restart.
/// Operations remain session-only; secrets and command output are never persisted.
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
    private readonly string? _storageRoot;
    private readonly ILogger<ServerDiagnostics>? _logger;

    private static readonly HashSet<string> PersistedKinds = new(StringComparer.Ordinal)
    {
        "UnexpectedStop", "RecoveryObserved", "ManualRestart", "Start", "Stop",
        "Backup", "Restore", "Update", "ApplyConfig", "RelayUnavailable", "LowDisk"
    };

    public ServerDiagnostics(string? storageRoot = null, ILogger<ServerDiagnostics>? logger = null)
    {
        _storageRoot = storageRoot;
        _logger = logger;
    }

    private string EventFile(string id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        return Path.Combine(_storageRoot!, hash + ".json");
    }

    private static string SafePersistedMessage(string kind) => kind switch
    {
        "UnexpectedStop" => "Server process exited unexpectedly.",
        "RecoveryObserved" => "Server process was detected running again.",
        "ManualRestart" => "Restart requested by an operator.",
        "Start" => "Server start event.",
        "Stop" => "Server stop event.",
        "RelayUnavailable" => "Public VPS Relay synchronization failed.",
        "LowDisk" => "Low disk space detected on the server volume.",
        _ => kind + " operation event; see Agent logs for details."
    };

    private static void RestoreEvent(State state, DiagnosticEvent evt)
    {
        if (!PersistedKinds.Contains(evt.Kind)) return;
        // Never trust message text on disk; persisted history contains generic summaries only.
        var safe = evt with { Message = SafePersistedMessage(evt.Kind) };
        state.Events.Enqueue(safe);
        if (safe.Kind == "UnexpectedStop") state.LastUnexpectedStopAt = safe.At;
        if (safe.Kind == "RecoveryObserved") state.LastRecoveryObservedAt = safe.At;
        if (safe.Kind == "ManualRestart") state.LastManualRestartAt = safe.At;
    }

    private State LoadState(string id)
    {
        var state = new State();
        if (_storageRoot is null) return state;
        try
        {
            var file = EventFile(id);
            if (!File.Exists(file) || new FileInfo(file).Length > 512 * 1024) return state;
            var history = JsonSerializer.Deserialize<DiagnosticEvent[]>(File.ReadAllText(file)) ?? [];
            foreach (var evt in history.TakeLast(MaxEvents))
                RestoreEvent(state, evt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogWarning("Could not load stored diagnostics for server {Id} ({Type})", id, ex.GetType().Name);
        }
        return state;
    }

    private void SaveEvents(string id, State state)
    {
        if (_storageRoot is null) return;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(_storageRoot);
            var file = EventFile(id);
            temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var safe = state.Events.Where(e => PersistedKinds.Contains(e.Kind))
                .Select(e => e with { Message = SafePersistedMessage(e.Kind) }).ToArray();
            File.WriteAllText(temp, JsonSerializer.Serialize(safe));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("Could not store diagnostics for server {Id} ({Type})", id, ex.GetType().Name);
        }
        finally
        {
            if (temp is not null)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void Record(string id, string kind, string severity, string message)
    {
        var state = _states.GetOrAdd(id, LoadState);
        lock (state.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            state.Events.Enqueue(new DiagnosticEvent(now, kind, severity, message));
            while (state.Events.Count > MaxEvents) state.Events.Dequeue();
            if (kind == "UnexpectedStop") state.LastUnexpectedStopAt = now;
            if (kind == "RecoveryObserved") state.LastRecoveryObservedAt = now;
            if (kind == "ManualRestart") state.LastManualRestartAt = now;
            if (PersistedKinds.Contains(kind)) SaveEvents(id, state);
        }
    }

    public DiagnosticSnapshot Snapshot(string id)
    {
        var state = _states.GetOrAdd(id, LoadState);
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
        var state = _states.GetOrAdd(id, LoadState);
        lock (state.Gate)
            state.Operations[operation] = new DiagnosticOperation(DateTimeOffset.UtcNow, null, "Running", "In progress");
        Record(id, operation, "Info", operation + " started");
    }

    private void Finish(string id, string operation, bool success, string message)
    {
        var state = _states.GetOrAdd(id, LoadState);
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
