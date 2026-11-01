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
        var def = _registry.Get(serverId);
        if (def is null || string.IsNullOrWhiteSpace(def.LogPathInGuest)) return false;
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Log tail not yet supported for HostingMode={Mode}", def.HostingMode);
            return false;
        }

        var path = def.LogPathInGuest!;
        var handle = _active.GetOrAdd(serverId, _ =>
        {
            var cts = new CancellationTokenSource();
            var created = new TailHandle(cts);
            created.Task = Task.Run(() => TailLoop(serverId, path, cts.Token));
            return created;
        });

        // A tail loop intentionally starts at EOF so it never floods clients with an
        // entire historical log. When the UI is opened, send a small bounded backlog
        // separately so the console is immediately useful even if the server is idle.
        _ = Task.Run(() => EmitRecentAsync(serverId, path, 100, CancellationToken.None));
        return handle.Cts.IsCancellationRequested == false;
    }

    public bool Stop(string serverId)
    {
        if (!_active.TryRemove(serverId, out var h)) return false;
        try { h.Cts.Cancel(); } catch { /* ignore */ }
        return true;
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

    private async Task EmitRecentAsync(string serverId, string path, int maxLines, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return;

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
            if (toRead <= 0) return;

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

            if (recent.Length == 0) return;
            await EmitAsync(serverId, $"[recent log — last {recent.Length} lines]");
            foreach (var line in recent)
                await EmitAsync(serverId, line);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "recent log read failed for {Path}", path);
        }
    }

    private async Task EmitAsync(string serverId, string text)
    {
        var line = new LogLine(serverId, DateTimeOffset.UtcNow, "log", text);
        try { await _hub.Clients.All.SendAsync("logLine", line); }
        catch { /* hub may not be ready at startup */ }
    }

    public void Dispose()
    {
        foreach (var kv in _active.ToArray()) Stop(kv.Key);
    }

    private sealed class TailHandle
    {
        public TailHandle(CancellationTokenSource cts) { Cts = cts; }
        public CancellationTokenSource Cts { get; }
        public Task? Task { get; set; }
    }
}
