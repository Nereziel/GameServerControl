using System.Reflection;
using GameServerControl.Runner;
using GameServerControl.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class RunnerConsoleDeltaTests
{
    [Fact]
    public void Console_state_returns_full_history_then_deltas_and_resyncs_after_sequence_reset()
    {
        var manifest = new RunnerManifest(
            "alpha",
            @"C:\Games\Alpha\server.exe",
            @"C:\Games\Alpha",
            Array.Empty<string>(),
            InteractiveConsole: false);
        var host = new RunnerProcessHost(manifest, NullLogger<RunnerProcessHost>.Instance);
        var recordConsole = typeof(RunnerProcessHost).GetMethod(
            "RecordConsole",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(recordConsole);

        recordConsole.Invoke(host, new object[] { "stdout", "one" });
        recordConsole.Invoke(host, new object[] { "stdout", "two" });
        recordConsole.Invoke(host, new object[] { "stdout", "three" });

        var initial = host.ConsoleState().Console!;
        Assert.Equal(3, initial.LastSequence);
        Assert.Equal(new long[] { 1, 2, 3 }, initial.Lines.Select(line => line.Sequence));

        var delta = host.ConsoleState(afterSequence: 2).Console!;
        Assert.Equal(3, delta.LastSequence);
        Assert.Equal(new long[] { 3 }, delta.Lines.Select(line => line.Sequence));

        var afterRunnerRestart = host.ConsoleState(afterSequence: 99).Console!;
        Assert.Equal(3, afterRunnerRestart.LastSequence);
        Assert.Equal(new long[] { 1, 2, 3 }, afterRunnerRestart.Lines.Select(line => line.Sequence));
    }
}
