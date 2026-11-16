using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GameServerControl.Agent.Servers;

/// <summary>Validates the machine-wide Java runtime before downloading server JARs.</summary>
public static class MinecraftJavaRuntime
{
    public static int? ParseMajorVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output,
            @"(?:openjdk|java)\s+version\s+""(?<major>[0-9]+)(?:\.(?<minor>[0-9]+))?",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major))
            return null;
        if (major == 1)
            return int.TryParse(match.Groups["minor"].Value, out var legacy) ? legacy : null;
        return major > 0 ? major : null;
    }

    public static int? MinimumJavaMajor(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return null;
        var match = Regex.Match(minecraftVersion, @"^1\.(?<minor>[0-9]+)(?:\.(?<patch>[0-9]+))?$");
        if (!match.Success || !int.TryParse(match.Groups["minor"].Value, out var minor))
            return null;
        var patch = int.TryParse(match.Groups["patch"].Value, out var p) ? p : 0;
        if (minor > 20 || minor == 20 && patch >= 5) return 21;
        if (minor >= 18) return 17;
        if (minor == 17) return 16;
        return null; // Older Minecraft distributions have differing requirements.
    }

    public static async Task<int> DetectMajorAsync(string javaExecutable, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(javaExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-version");
        if (!process.Start())
            throw new InvalidOperationException("Could not start Java to determine its runtime version.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdOut = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stdErr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdOut) + "\n" + (await stdErr);
            var version = ParseMajorVersion(output);
            if (process.ExitCode != 0 || version is null)
                throw new InvalidOperationException("Java -version did not report a supported runtime.");
            return version.Value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Java -version timed out after 10 seconds.");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
