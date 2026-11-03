using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, TailHandle> _active = new();

    public LogTailService(IHubContext<StatusHub> hub, ServerRegistry registry, ILogger<LogTailService> logger)
    {
        _hub = hub;
        _registry = registry;
        _logger = logger;
    }

    public bool IsTailing(string serverId) => _active.ContainsKey(serverId);

    public bool Start(string serverId)
    {
        if (!TryResolveTailPath(serverId, out var path)) return false;
        return EnsureActive(serverId, path, handle => handle.ManualStart = true);
    }

    public bool Subscribe(string serverId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return false;
        if (!TryResolveTailPath(serverId, out var path)) return false;
        return EnsureActive(serverId, path, handle => handle.Subscribers.Add(connectionId));
    }

    public void UnsubscribeConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;

        foreach (var entry in _active.ToArray())
        {
            var handle = entry.Value;
            var cancel = false;
            lock (handle.Gate)
            {
                if (!_active.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, handle))
                    continue;
                if (!handle.Subscribers.Remove(connectionId))
                    continue;
                if (handle.Subscribers.Count != 0 || handle.ManualStart)
                    continue;

                if (_active.TryRemove(entry.Key, out var removed) && ReferenceEquals(removed, handle))
                    cancel = true;
            }

            if (cancel) Cancel(handle);
        }
    }

    public bool Stop(string serverId)
    {
        if (!_active.TryGetValue(serverId, out var handle)) return false;

        var removed = false;
        lock (handle.Gate)
        {
            if (!_active.TryGetValue(serverId, out var current) || !ReferenceEquals(current, handle))
                return false;
            if (_active.TryRemove(serverId, out var removedHandle) && ReferenceEquals(removedHandle, handle))
                removed = true;
        }

        if (removed) Cancel(handle);
        return removed;
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

    private bool TryResolveTailPath(string serverId, out string path)
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

    private bool EnsureActive(string serverId, string path, Action<TailHandle> register)
    {
        while (true)
        {
            var handle = GetOrAddHandle(serverId);
            lock (handle.Gate)
            {
                if (handle.Cts.IsCancellationRequested
                    || !_active.TryGetValue(serverId, out var current)
                    || !ReferenceEquals(current, handle))
                    continue;

                register(handle);
                handle.Task ??= Task.Run(() => TailLoop(serverId, path, handle.Cts.Token));
                return true;
            }
        }
    }

    private TailHandle GetOrAddHandle(string serverId)
    {
        if (_active.TryGetValue(serverId, out var existing)) return existing;

        var candidate = new TailHandle(new CancellationTokenSource());
        var handle = _active.GetOrAdd(serverId, candidate);
        if (!ReferenceEquals(handle, candidate))
            candidate.Cts.Dispose();
        return handle;
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
        foreach (var kv in _active.ToArray()) Stop(kv.Key);
    }

    private sealed class TailHandle
    {
        public TailHandle(CancellationTokenSource cts) { Cts = cts; }
        public object Gate { get; } = new();
        public CancellationTokenSource Cts { get; }
        public HashSet<string> Subscribers { get; } = new(StringComparer.Ordinal);
        public bool ManualStart { get; set; }
        public Task? Task { get; set; }
    }
}
