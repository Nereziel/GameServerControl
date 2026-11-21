using GameServerControl.Agent.Servers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class SteamCmdBootstrapRetryTests
{
    [Fact]
    public async Task Bootstrap_exit_seven_retries_even_when_previous_app_state_was_error_six()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewTestDirectory();
        try
        {
            var counter = Path.Combine(root, "attempts");
            var script = Path.Combine(root, "steamcmd-stub");
            MakeExecutable(script, $"""
                #!/bin/sh
                count=$(cat '{counter}' 2>/dev/null || echo 0)
                count=$((count + 1))
                echo "$count" > '{counter}'
                if [ "$count" -eq 1 ]; then
                  echo "Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)"
                  echo "Error! App '4019830' state is 0x6 after update job."
                  echo "[----] Update complete, launching Steamcmd..."
                  exit 7
                fi
                echo "Success! App '4019830' fully installed."
                exit 0
                """);

            var manager = CreateManager(script);
            var lines = new List<string>();
            var result = await manager.RunAppUpdateAsync(
                Path.Combine(root, "game"), "4019830", (line, _) => lines.Add(line), CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("2", File.ReadAllText(counter).Trim());
            Assert.Contains(lines, line => line.Contains("retrying the game update once", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Success! App '4019830'", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Bootstrap_error_with_subscription_denial_does_not_retry()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewTestDirectory();
        try
        {
            var counter = Path.Combine(root, "attempts");
            var script = Path.Combine(root, "steamcmd-stub");
            MakeExecutable(script, $"""
                #!/bin/sh
                echo called >> '{counter}'
                echo "Error! App '4019830' state is 0x6 after update job."
                echo "No subscription"
                echo "[----] Update complete, launching Steamcmd..."
                exit 7
                """);

            var manager = CreateManager(script);
            var result = await manager.RunAppUpdateAsync(
                Path.Combine(root, "game"), "4019830", (_, _) => { }, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(7, result.ExitCode);
            Assert.Single(File.ReadAllLines(counter));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Exit_zero_without_steamcmd_success_marker_is_not_reported_as_updated()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = NewTestDirectory();
        try
        {
            var script = Path.Combine(root, "steamcmd-stub");
            MakeExecutable(script, """
                #!/bin/sh
                echo "Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)"
                exit 0
                """);

            var manager = CreateManager(script);
            var lines = new List<string>();
            var result = await manager.RunAppUpdateAsync(
                Path.Combine(root, "game"), "4019830", (line, _) => lines.Add(line), CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(lines, line => line.Contains("without confirming App '4019830'", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gsc-steamcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakeExecutable(string path, string content)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static SteamCmdManager CreateManager(string scriptPath)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:SteamCmdPath"] = scriptPath
        }).Build();
        return new SteamCmdManager(config, NullLogger<SteamCmdManager>.Instance);
    }
}
