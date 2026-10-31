using System.Diagnostics;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Networking;

/// <summary>
/// Owns the persistent Windows Firewall program rules created by GameServerControl.
/// For now firewall access is intentionally program-based rather than port-based:
/// the per-server NetworkPorts metadata is used for collision detection and future
/// port-rule support, while Windows receives one inbound and one outbound allow rule
/// for the configured server executable.
/// </summary>
public static class WindowsFirewall
{
    private const string Prefix = "GameServerControl - ";

    public static bool EnsureProgramRules(ServerDef def, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) return true;

        var applicable = def.HostingMode == HostingMode.BareMetal
            && !string.IsNullOrWhiteSpace(def.GuestExePath)
            && string.Equals(Path.GetExtension(def.GuestExePath), ".exe", StringComparison.OrdinalIgnoreCase);

        if (!applicable)
        {
            // If the server used to be BareMetal/.exe, remove stale rules when it moves to
            // Hyper-V or changes to a .bat/.jar launcher.
            return RemoveProgramRules(def.Id, out error);
        }

        string exe;
        try { exe = Path.GetFullPath(def.GuestExePath); }
        catch (Exception ex) { error = ex.Message; return false; }

        // Recreate our two deterministic rules so changing the executable path is idempotent.
        RemoveProgramRules(def.Id, out _);

        var inbound = AddRule(RuleName(def.Id, inbound: true), "in", exe);
        var outbound = AddRule(RuleName(def.Id, inbound: false), "out", exe);
        if (inbound.Ok && outbound.Ok) return true;

        error = string.Join(" | ", new[] { inbound.Error, outbound.Error }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return false;
    }

    public static bool RemoveProgramRules(string serverId, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) return true;

        var a = DeleteRule(RuleName(serverId, inbound: true));
        var b = DeleteRule(RuleName(serverId, inbound: false));
        if (a.Ok && b.Ok) return true;
        error = string.Join(" | ", new[] { a.Error, b.Error }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return false;
    }

    private static string RuleName(string id, bool inbound)
        => Prefix + id + (inbound ? " - Inbound" : " - Outbound");

    private static (bool Ok, string Error) AddRule(string name, string direction, string exe)
        => RunNetsh(
            "advfirewall", "firewall", "add", "rule",
            $"name={name}", $"dir={direction}", "action=allow", $"program={exe}",
            "enable=yes", "profile=any");

    private static (bool Ok, string Error) DeleteRule(string name)
        => RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={name}");

    private static (bool Ok, string Error) RunNetsh(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);
            if (p is null) return (false, "Could not start netsh.exe.");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, "netsh.exe timed out.");
            }

            if (p.ExitCode == 0) return (true, "");
            var text = string.Join(" ", new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return (false, string.IsNullOrWhiteSpace(text) ? $"netsh.exe exited with code {p.ExitCode}." : text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}