using System.Reflection;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class RunnerProtocolTests
{
    [Fact]
    public void Runner_identity_is_deterministic_distinct_and_path_safe()
    {
        var alpha = RunnerIdentity.ForServer("alpha");
        var alphaAgain = RunnerIdentity.ForServer("alpha");
        var beta = RunnerIdentity.ForServer("beta");
        var hostile = RunnerIdentity.ForServer("../../evil\\name");

        Assert.Equal("8ed3f6ad685b959e", alpha.Key);
        Assert.Equal(alpha, alphaAgain);
        Assert.NotEqual(alpha.ServiceName, beta.ServiceName);
        Assert.DoesNotContain("..", hostile.ServiceName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', hostile.ServiceName);
        Assert.DoesNotContain('\\', hostile.ServiceName);
        Assert.DoesNotContain("..", hostile.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', hostile.PipeName);
        Assert.DoesNotContain('\\', hostile.PipeName);
    }

    [Theory]
    [InlineData(@"Games\Alpha\server.exe", @"C:\Games\Alpha")]
    [InlineData(@"C:\Games\Alpha\server.exe", @"Games\Alpha")]
    public void Runner_manifest_rejects_relative_paths(string executable, string workingDirectory)
    {
        var manifest = Manifest(executable, workingDirectory, new[] { "helper.exe" });

        Assert.NotNull(manifest.Validate());
    }

    [Theory]
    [InlineData(@"sub\helper.exe")]
    [InlineData("sub/helper.exe")]
    public void Runner_manifest_rejects_path_valued_stop_process_names(string stopProcessName)
    {
        var manifest = Manifest(@"C:\Games\Alpha\server.exe", @"C:\Games\Alpha", new[] { stopProcessName });

        Assert.NotNull(manifest.Validate());
    }

    [Fact]
    public void Runner_manifest_accepts_rooted_windows_paths_and_leaf_process_names()
    {
        var manifest = Manifest(@"C:\Games\Alpha\server.exe", @"C:\Games\Alpha", new[] { "helper.exe" });

        Assert.Null(manifest.Validate());
    }

    [Fact]
    public void Runner_request_schema_cannot_carry_launch_metadata()
    {
        var names = typeof(RunnerRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(new[] { "AfterSequence", "Command", "ConsoleInput", "Force" }.OrderBy(x => x), names);
    }

    [Fact]
    public void Runner_request_console_cursor_is_nonnegative_and_console_state_only()
    {
        var property = typeof(RunnerRequest).GetProperty("AfterSequence", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);

        var ctor = typeof(RunnerRequest).GetConstructors().Single();
        var valid = (RunnerRequest)ctor.Invoke(new object?[] { RunnerCommand.ConsoleState, false, null, 12L });
        var negative = (RunnerRequest)ctor.Invoke(new object?[] { RunnerCommand.ConsoleState, false, null, -1L });
        var wrongCommand = (RunnerRequest)ctor.Invoke(new object?[] { RunnerCommand.Start, false, null, 12L });

        Assert.Null(valid.Validate());
        Assert.NotNull(negative.Validate());
        Assert.NotNull(wrongCommand.Validate());
    }

    [Fact]
    public void Runner_console_poll_cursor_is_owned_by_each_pump_request()
    {
        var poll = typeof(IWindowsRunnerServiceManager).GetMethod(nameof(IWindowsRunnerServiceManager.PollConsoleStateAsync));
        Assert.NotNull(poll);

        Assert.Equal(
            new[] { typeof(ServerDef), typeof(long), typeof(CancellationToken) },
            poll.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void Runner_request_rejects_console_input_for_non_console_commands()
    {
        var request = new RunnerRequest(RunnerCommand.Start, ConsoleInput: "unexpected");

        Assert.NotNull(request.Validate());
    }

    [Fact]
    public void Runner_console_input_accepts_one_bounded_command_and_rejects_invalid_input()
    {
        Assert.Null(new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: "save").Validate());
        Assert.NotNull(new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: " ").Validate());
        Assert.NotNull(new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: "save\nquit").Validate());
        Assert.NotNull(new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: new string('x', RunnerProtocolLimits.MaxConsoleInputChars + 1)).Validate());
    }

    private static RunnerManifest Manifest(string executable, string workingDirectory, string[] stopProcessNames) =>
        new(
            "alpha",
            executable,
            workingDirectory,
            new[] { "-port", "2456" },
            false,
            "896660",
            stopProcessNames,
            false);
}
