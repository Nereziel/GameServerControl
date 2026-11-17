using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ProcessMetricsSamplerTests
{
    private static ServerDef Definition() => new(
        Id: "current-process",
        Name: "Current process",
        VmName: "",
        GameType: GameType.Custom,
        GuestExePath: "",
        GuestWorkingDir: "",
        StartArgs: Array.Empty<string>(),
        SaveDirs: Array.Empty<string>(),
        SteamAppId: null,
        GuestCredentialId: null,
        LogPathInGuest: null,
        HostingMode: HostingMode.BareMetal);

    [Fact]
    public void Memory_is_available_immediately_and_cpu_after_second_sample()
    {
        var sampler = new ProcessMetricsSampler();
        var def = Definition();
        var initial = sampler.Sample(def, Environment.ProcessId);
        Assert.Null(initial.CpuPercent);
        Assert.True(initial.MemoryMB > 0);

        Thread.Sleep(160);
        var next = sampler.Sample(def, Environment.ProcessId);
        Assert.NotNull(next.CpuPercent);
        Assert.InRange(next.CpuPercent!.Value, 0, 100);
        Assert.True(next.MemoryMB > 0);
    }

    [Fact]
    public void Missing_process_does_not_report_misleading_values()
    {
        var result = new ProcessMetricsSampler().Sample(Definition(), int.MaxValue);
        Assert.Null(result.CpuPercent);
        Assert.Null(result.MemoryMB);
    }
}
