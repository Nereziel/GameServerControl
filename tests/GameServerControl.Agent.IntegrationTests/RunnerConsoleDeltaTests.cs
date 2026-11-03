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
        var manifest = Manifest();
        var host = new RunnerProcessHost(manifest, NullLogger<RunnerProcessHost>.Instance);
        var recordConsole = RecordConsoleMethod();

        Record(recordConsole, host, "one");
        Record(recordConsole, host, "two");
        Record(recordConsole, host, "three");

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

    [Fact]
    public void Console_state_generation_mismatch_resyncs_even_when_new_sequence_passed_old_cursor()
    {
        var manifest = Manifest();
        var firstHost = new RunnerProcessHost(manifest, NullLogger<RunnerProcessHost>.Instance);
        var secondHost = new RunnerProcessHost(manifest, NullLogger<RunnerProcessHost>.Instance);
        var recordConsole = RecordConsoleMethod();
        var generationProperty = typeof(RunnerConsoleSnapshot).GetProperty("Generation", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(generationProperty);
        var consoleStateWithGeneration = typeof(RunnerProcessHost).GetMethod(
            nameof(RunnerProcessHost.ConsoleState),
            new[] { typeof(long?), typeof(string) });
        Assert.NotNull(consoleStateWithGeneration);

        Record(recordConsole, firstHost, "old-one");
        Record(recordConsole, firstHost, "old-two");
        Record(recordConsole, firstHost, "old-three");
        var firstSnapshot = firstHost.ConsoleState().Console!;
        var firstGeneration = Assert.IsType<string>(generationProperty.GetValue(firstSnapshot));

        for (var i = 1; i <= 5; i++)
            Record(recordConsole, secondHost, "new-" + i);

        var response = Assert.IsType<RunnerResponse>(consoleStateWithGeneration.Invoke(secondHost, new object?[] { 3L, firstGeneration }));
        var snapshot = Assert.IsType<RunnerConsoleSnapshot>(response.Console);
        var secondGeneration = Assert.IsType<string>(generationProperty.GetValue(snapshot));

        Assert.NotEqual(firstGeneration, secondGeneration);
        Assert.Equal(5, snapshot.LastSequence);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, snapshot.Lines.Select(line => line.Sequence));
    }

    private static RunnerManifest Manifest() =>
        new(
            "alpha",
            @"C:\Games\Alpha\server.exe",
            @"C:\Games\Alpha",
            Array.Empty<string>(),
            InteractiveConsole: false);

    private static MethodInfo RecordConsoleMethod()
    {
        var method = typeof(RunnerProcessHost).GetMethod(
            "RecordConsole",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<MethodInfo>(method);
    }

    private static void Record(MethodInfo recordConsole, RunnerProcessHost host, string text)
        => recordConsole.Invoke(host, new object[] { "stdout", text });
}
