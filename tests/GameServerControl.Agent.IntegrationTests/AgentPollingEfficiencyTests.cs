using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class AgentPollingEfficiencyTests
{
    [Fact]
    public void Running_and_uncertain_servers_are_always_polled()
    {
        var policy = new ServerStatusPollPolicy();
        var now = DateTimeOffset.UtcNow;
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Running, now));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Running, now.AddSeconds(5)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Unknown, now.AddSeconds(6)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Starting, now.AddSeconds(7)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Stopping, now.AddSeconds(8)));
    }

    [Fact]
    public void Stopped_server_queries_are_throttled_but_manual_start_is_immediate()
    {
        var policy = new ServerStatusPollPolicy();
        var now = DateTimeOffset.UtcNow;
        Assert.True(policy.ShouldPoll("alpha", ProcessState.NotRunning, now));
        Assert.False(policy.ShouldPoll("alpha", ProcessState.NotRunning, now.AddSeconds(5)));
        Assert.False(policy.ShouldPoll("alpha", ProcessState.NotRunning, now.AddSeconds(10)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.NotRunning, now.AddSeconds(15)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.Running, now.AddSeconds(16)));
        Assert.True(policy.ShouldPoll("alpha", ProcessState.NotRunning, now.AddSeconds(20)));
        Assert.True(policy.ShouldPoll("beta", ProcessState.NotRunning, now.AddSeconds(20)));
    }

    private static ServerStatus Status(ProcessState state = ProcessState.NotRunning,
        Dictionary<string,string>? metadata = null) =>
        new("alpha", VmState.NotApplicable, state, null, null, null, null,
            null, null, metadata);

    [Fact]
    public void Broadcast_filter_ignores_identical_states_and_equal_metadata_values()
    {
        var left = Status(metadata: new Dictionary<string,string> { ["InviteCode"]="ABC", ["ServerName"]="A" });
        var same = Status(metadata: new Dictionary<string,string> { ["ServerName"]="A", ["InviteCode"]="ABC" });
        Assert.True(ServerStatusChangeDetector.HasChanged(null, left));
        Assert.False(ServerStatusChangeDetector.HasChanged(left, same));
        Assert.True(ServerStatusChangeDetector.HasChanged(left, same with { ProcessState = ProcessState.Running }));
        Assert.True(ServerStatusChangeDetector.HasChanged(left, same with { MemoryMB = 123 }));
        Assert.True(ServerStatusChangeDetector.HasChanged(left, same with { LastError = "failure" }));
        Assert.True(ServerStatusChangeDetector.HasChanged(left,
            Status(metadata:new Dictionary<string,string> { ["InviteCode"]="XYZ", ["ServerName"]="A" })));
    }
}
