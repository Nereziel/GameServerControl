using System.Diagnostics;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ProcessCleanupScopeTests
{
    [Fact]
    public void Same_process_name_does_not_authorize_another_installation()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-scope-" + Guid.NewGuid().ToString("N"));
        var alphaRoot = Path.Combine(root, "alpha");
        var betaRoot = Path.Combine(root, "beta");
        var own = Path.Combine(alphaRoot, "bin", "game-server");
        var other = Path.Combine(betaRoot, "bin", "game-server");

        Assert.True(ProcessCleanupScope.IsAllowedExtra(own, alphaRoot, new[] { "game-server" }));
        Assert.False(ProcessCleanupScope.IsAllowedExtra(other, alphaRoot, new[] { "game-server" }));
        Assert.False(ProcessCleanupScope.IsAllowedExtra(own, alphaRoot, new[] { "unrelated-server" }));
        Assert.False(ProcessCleanupScope.IsAllowedExtra(Path.Combine(root, "alpha-sibling", "game-server"),
            alphaRoot, new[] { "game-server" }));
        var filesystemRoot = Path.GetPathRoot(alphaRoot)!;
        Assert.False(ProcessCleanupScope.IsAllowedExtra(
            Path.Combine(filesystemRoot, "game-server"), filesystemRoot, new[] { "game-server" }));
        Assert.False(ProcessCleanupScope.IsAllowedExtra(
            "game-server", alphaRoot, new[] { "game-server" }));
    }

    [Fact]
    public void Process_graph_excludes_sibling_server_and_breaks_cycles()
    {
        var descendants = ProcessCleanupScope.DescendantIds(100, new[]
        {
            new ProcessCleanupScope.ParentLink(100, 1),
            new ProcessCleanupScope.ParentLink(101, 100),
            new ProcessCleanupScope.ParentLink(102, 101),
            new ProcessCleanupScope.ParentLink(103, 100),
            new ProcessCleanupScope.ParentLink(200, 1),
            new ProcessCleanupScope.ParentLink(201, 200),
            new ProcessCleanupScope.ParentLink(100, 102)
        });

        Assert.Equal(new[] { 101, 102, 103 }, descendants.OrderBy(x => x).ToArray());
        Assert.DoesNotContain(201, descendants);
        Assert.DoesNotContain(100, descendants);
    }

    [Fact]
    public void Linux_ownership_requires_both_the_working_directory_and_executable_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-owners-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "alpha");
        var b = Path.Combine(root, "beta");
        var allowed = new[] { "server.bin" };

        Assert.True(ProcessCleanupScope.IsOwnedLinuxExtra(
            Path.Combine(a, "server.bin"), a, a, allowed));
        Assert.False(ProcessCleanupScope.IsOwnedLinuxExtra(
            Path.Combine(b, "server.bin"), a, a, allowed));
        Assert.False(ProcessCleanupScope.IsOwnedLinuxExtra(
            Path.Combine(a, "server.bin"), b, a, allowed));
        Assert.False(ProcessCleanupScope.IsOwnedLinuxExtra(
            Path.Combine(a, "server.bin"), null, a, allowed));
    }

    [Fact]
    public async Task Linux_cleanup_refuses_a_shared_working_directory()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var factory = new AgentTestFactory();
        var servers = JsonNode.Parse(File.ReadAllText(factory.ServersPath))!;
        var sharedRoot = servers["Servers"]![0]!["GuestWorkingDir"]!.GetValue<string>();
        servers["Servers"]![1]!["GuestWorkingDir"] = sharedRoot;
        File.WriteAllText(factory.ServersPath, servers.ToJsonString());

        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var local = factory.Services.GetRequiredService<LocalProcessService>();
        var binary = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        var executable = Path.Combine(sharedRoot, "gscsleep");
        File.Copy(binary, executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var process = Process.Start(new ProcessStartInfo(executable, "30")
        {
            UseShellExecute = false,
            WorkingDirectory = sharedRoot
        })!;
        try
        {
            await local.KillByNamesAsync(registry.Get("alpha")!, new[] { "gscsleep" });
            Assert.False(process.HasExited);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Linux_cleanup_only_kills_process_with_own_install_and_cwd()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var local = factory.Services.GetRequiredService<LocalProcessService>();
        var binary = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";

        var alphaRoot = registry.Get("alpha")!.GuestWorkingDir;
        var betaRoot = registry.Get("beta")!.GuestWorkingDir;
        var aExe = Path.Combine(alphaRoot, "gscsleep");
        var bExe = Path.Combine(betaRoot, "gscsleep");
        File.Copy(binary, aExe);
        File.Copy(binary, bExe);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(aExe, mode);
        File.SetUnixFileMode(bExe, mode);

        using var alpha = Process.Start(new ProcessStartInfo(aExe, "30")
        {
            UseShellExecute = false,
            WorkingDirectory = alphaRoot
        })!;
        using var beta = Process.Start(new ProcessStartInfo(bExe, "30")
        {
            UseShellExecute = false,
            WorkingDirectory = betaRoot
        })!;

        try
        {
            Assert.Equal(2, Process.GetProcessesByName("gscsleep")
                .Count(p => p.Id == alpha.Id || p.Id == beta.Id));
            await local.KillByNamesAsync(registry.Get("alpha")!, new[] { "gscsleep" });
            await alpha.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(alpha.HasExited);
            Assert.False(beta.HasExited);
        }
        finally
        {
            try { if (!alpha.HasExited) alpha.Kill(entireProcessTree: true); } catch { }
            try { if (!beta.HasExited) beta.Kill(entireProcessTree: true); } catch { }
            try { await alpha.WaitForExitAsync(); } catch { }
            try { await beta.WaitForExitAsync(); } catch { }
        }
    }
}
