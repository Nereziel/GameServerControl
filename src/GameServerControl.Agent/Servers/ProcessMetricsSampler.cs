using System.Collections.Concurrent;
using System.Diagnostics;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Samples the actual game workload rather than only the launcher PID.
/// CPU requires two observations, while working-set memory is available immediately.
/// </summary>
public sealed class ProcessMetricsSampler
{
    private readonly ConcurrentDictionary<(string ServerId, int Pid), (TimeSpan Cpu, DateTime At)> _samples = new();

    public (double? CpuPercent, long? MemoryMB) Sample(ServerDef def, int rootPid)
    {
        var pids = new HashSet<int> { rootPid };

        // Dragonwilds starts a small launcher and then the actual Unreal server
        // in a second process. Limit discovery to this installation's exact path
        // so an unrelated instance is never included in this server's metrics.
        if (OperatingSystem.IsWindows() &&
            (string.Equals(def.GameProfileKey, "dragonwilds", StringComparison.OrdinalIgnoreCase) ||
             def.SteamAppId == "4019830"))
        {
            var installRoot = Path.GetDirectoryName(def.GuestExePath) ?? def.GuestWorkingDir;
            var shippingExe = Path.Combine(installRoot, "RSDragonwilds", "Binaries", "Win64",
                "RSDragonwildsServer-Win64-Shipping.exe");
            foreach (var candidate in Process.GetProcessesByName("RSDragonwildsServer-Win64-Shipping"))
            {
                using (candidate)
                {
                    try
                    {
                        if (string.Equals(candidate.MainModule?.FileName, shippingExe,
                                StringComparison.OrdinalIgnoreCase))
                            pids.Add(candidate.Id);
                    }
                    catch (System.ComponentModel.Win32Exception) { /* inaccessible process */ }
                    catch (InvalidOperationException) { /* process exited */ }
                }
            }
        }

        var now = DateTime.UtcNow;
        long workingSetBytes = 0;
        var memoryReadCount = 0;
        var cpuReadCount = 0;
        double totalCpuPercent = 0;

        foreach (var pid in pids)
        {
            var key = (def.Id, pid);
            try
            {
                using var process = Process.GetProcessById(pid);
                workingSetBytes += process.WorkingSet64;
                memoryReadCount++;

                var cpu = process.TotalProcessorTime;
                if (_samples.TryGetValue(key, out var previous))
                {
                    var elapsed = (now - previous.At).TotalMilliseconds;
                    var consumed = (cpu - previous.Cpu).TotalMilliseconds;
                    if (elapsed > 100 && consumed >= 0)
                    {
                        totalCpuPercent += consumed / elapsed / Environment.ProcessorCount * 100;
                        cpuReadCount++;
                    }
                }
                _samples[key] = (cpu, now);
            }
            catch (ArgumentException) { _samples.TryRemove(key, out _); }
            catch (InvalidOperationException) { _samples.TryRemove(key, out _); }
            catch (System.ComponentModel.Win32Exception) { _samples.TryRemove(key, out _); }
        }

        // Retire samples for processes that are no longer part of this instance.
        foreach (var key in _samples.Keys)
            if (key.ServerId == def.Id && !pids.Contains(key.Pid))
                _samples.TryRemove(key, out _);

        return (
            cpuReadCount > 0 ? Math.Round(Math.Clamp(totalCpuPercent, 0, 100), 1) : null,
            memoryReadCount > 0 ? workingSetBytes / (1024 * 1024) : null
        );
    }
}
