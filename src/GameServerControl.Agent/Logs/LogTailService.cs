using System.Text;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Logs;

/// <summary>
/// Per-server background log tail. While a server's tail is running, new lines
/// appended to its LogPathInGuest are pushed to SignalR clients as LogLine events
/// (source = "log"). Multi-client safe; each server gets at most one tail loop.
/// </summary>
public sealed class LogTailService : IDisposable
{
    private readonly IHubContext<StatusHub> _hub;
    private readonly ServerRegistry _registry;
    private readonly ILogger<LogTailService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, TailHandle> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _serversByConnection = new(StringComparer.Ordinal);

    public LogTailService(IHubContext<StatusHub> hub, ServerRegistry registry, ILogger<LogTailService> logger)
    {
        _hub = hub;
        _registry = registry;
        _logger = logger;
    }

    public bool IsTailing(string serverId)
    {
        lock (_gate)
            return _active.TryGetValue(serverId, out var handle) && !handle.Cts.IsCancellationRequested;
    }

    public bool Start(string serverId)
    {
        if (!TryGetTailPath(serverId, out var path)) return false;

        lock (_gate)
        {
            var handle = EnsureTailLocked(serverId, path);
            handle.ManualRequested = true;
            return !handle.Cts.IsCancellationRequested;
        }
    }

    public bool Stop(string serverId)
    {
        TailHandle? handle;
        lock (_gate)
        {
            if (!_active.Remove(serverId, out handle)) return false;
        }

        Cancel(handle);
        return true;
    }

    public bool Subscribe(string serverId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return false;
        if (!TryGetTailPath(serverId, out var path)) return false;

        lock (_gate)
        {
            var handle = EnsureTailLocked(serverId, path);
            handle.Subscribers.Add(connectionId);

            if (!_serversByConnection.TryGetValue(connectionId, out var servers))
            {
                servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _serversByConnection[connectionId] = servers;
            }
            servers.Add(serverId);
            return true;
        }
    }

    public void UnsubscribeConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        List<TailHandle>? toCancel = null;
        lock (_gate)
        {
            if (!_serversByConnection.Remove(connectionId, out var servers)) return;

            foreach (var serverId in servers)
            {
                if (!_active.TryGetValue(serverId, out var handle)) continue;
                handle.Subscribers.Remove(connectionId);
                if (handle.Subscribers.Count != 0 || handle.ManualRequested) continue;

                _active.Remove(serverId);
                (toCancel ??= new List<TailHandle>()).Add(handle);
            }
        }

        if (toCancel is null) return;
        foreach (var handle in toCancel) Cancel(handle);
    }

    public async Task<LogLine[]> GetRecentAsync(
        string serverId,
        int maxLines = 100,
        CancellationToken ct = default)
    {
        var def = _registry.Get(serverId);
        if (def is null || string.IsNullOrWhiteSpace(def.LogPathInGuest)) return Array.Empty<LogLine>();
        if (def.HostingMode != HostingMode.BareMetal) return Array.Empty<LogLine>();

        var path = def.LogPathInGuest!;
        try
        {
            if (!File.Exists(path)) return Array.Empty<LogLine>();

            const int maxBytes = 256 * 1024;
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                useAsync: true);

            var length = fs.Length;
            var toRead = (int)Math.Min(length, maxBytes);
            if (toRead <= 0) return Array.Empty<LogLine>();

            fs.Seek(-toRead, SeekOrigin.End);
            var buffer = new byte[toRead];
            var total = 0;
            while (total < toRead)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(total, toRead - total), ct);
                if (n == 0) break;
                total += n;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, total).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var skipPartialFirstLine = length > toRead ? 1 : 0;
            var recent = lines
                .Skip(skipPartialFirstLine)
                .Where(line => !string.IsNullOrEmpty(line))
                .TakeLast(Math.Clamp(maxLines, 1, 500))
                .ToArray();

            if (recent.Length == 0) return Array.Empty<LogLine>();

            var at = DateTimeOffset.UtcNow;
            var result = new LogLine[recent.Length + 1];
            result[0] = new LogLine(serverId, at, "log", $"[recent log — last {recent.Length} lines]");
            for (var i = 0; i < recent.Length; i++)
                result[i + 1] = new LogLine(serverId, at, "log", recent[i]);
            return result;
        }
        catch (OperationCanceledException) { return Array.Empty<LogLine>(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "recent log read failed for {Path}", path);
            return Array.Empty<LogLine>();
        }
    }

    private bool TryGetTailPath(string serverId, out string path)
    {
        path = "";
        var def = _registry.Get(serverId);
        if (def is null || string.IsNullOrWhiteSpace(def.LogPathInGuest)) return false;
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Log tail not yet supported for HostingMode={Mode}", def.HostingMode);
            return false;
        }

        path = def.LogPathInGuest!;
        return true;
    }

    private TailHandle EnsureTailLocked(string serverId, string path)
    {
        if (_active.TryGetValue(serverId, out var existing) && !existing.Cts.IsCancellationRequested)
            return existing;

        var cts = new CancellationTokenSource();
        var created = new TailHandle(cts);
        _active[serverId] = created;
        created.Task = Task.Run(() => TailLoop(serverId, path, cts.Token));
        return created;
    }

    private static void Cancel(TailHandle handle)
    {
        try { handle.Cts.Cancel(); } catch { /* ignore */ }
    }

    private async Task TailLoop(string serverId, string path, CancellationToken ct)
    {
        long position = 0;
        var buf = new byte[64 * 1024];
        var carry = new StringBuilder();

        // Start from the end of the file if it already exists, so we don't dump history.
        try { if (File.Exists(path)) position = new FileInfo(path).Length; }
        catch (Exception ex) { _logger.LogDebug(ex, "stat failed for {Path}", path); }

        await EmitAsync(serverId, $"[live log started — {path}]");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (position > fs.Length) position = 0; // log rotated
                fs.Seek(position, SeekOrigin.Begin);

                int n;
                while ((n = await fs.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    position += n;
                    carry.Append(Encoding.UTF8.GetString(buf, 0, n));
                    var s = carry.ToString();
                    var lines = s.Replace("\r\n", "\n").Split('\n');
                    // Keep last partial line in carry
                    carry.Clear();
                    carry.Append(lines[^1]);
                    for (var i = 0; i < lines.Length - 1; i++)
                    {
                        if (!string.IsNullOrEmpty(lines[i]))
                            await EmitAsync(serverId, lines[i]);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "tail iteration failed for {Path}", path);
                await Task.Delay(2000, ct);
                continue;
            }

            try { await Task.Delay(700, ct); } catch (OperationCanceledException) { break; }
        }

        await EmitAsync(serverId, "[live log stopped]");
    }

    private async Task EmitAsync(string serverId, string text)
    {
        var line = new LogLine(serverId, DateTimeOffset.UtcNow, "log", text);
        try { await _hub.Clients.Group(StatusHub.ServerGroup(serverId)).SendAsync("logLine", line); }
        catch { /* hub may not be ready at startup */ }
    }

    public void Dispose()
    {
        TailHandle[] handles;
        lock (_gate)
        {
            handles = _active.Values.ToArray();
            _active.Clear();
            _serversByConnection.Clear();
        }

        foreach (var handle in handles) Cancel(handle);
    }

    private sealed class TailHandle
    {
        public TailHandle(CancellationTokenSource cts) { Cts = cts; }
        public CancellationTokenSource Cts { get; }
        public Task? Task { get; set; }
        public bool ManualRequested { get; set; }
        public HashSet<string> Subscribers { get; } = new(StringComparer.Ordinal);
    }
}
