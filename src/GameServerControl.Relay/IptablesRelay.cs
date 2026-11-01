using System.Diagnostics;
using System.Net;
using GameServerControl.Shared;

namespace GameServerControl.Relay;

public sealed class IptablesRelay
{
    private const string DnatChain = "GSC_RELAY_DNAT";
    private const string SnatChain = "GSC_RELAY_SNAT";
    private const string ForwardChain = "GSC_RELAY_FWD";

    private readonly string _iptables;
    private readonly string _publicInterface;
    private readonly string _tailscaleInterface;
    private readonly bool _allowNonTailscaleTargets;
    private readonly ILogger<IptablesRelay> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Rule(string? Table, string Chain, string[] Args);

    public IptablesRelay(IConfiguration configuration, ILogger<IptablesRelay> logger)
    {
        _iptables = configuration["Relay:IptablesPath"] ?? "iptables";
        _publicInterface = configuration["Relay:PublicInterface"] ?? "";
        _tailscaleInterface = configuration["Relay:TailscaleInterface"] ?? "tailscale0";
        _allowNonTailscaleTargets = bool.TryParse(configuration["Relay:AllowNonTailscaleTargets"], out var allow) && allow;
        _logger = logger;
    }

    public string PublicInterface => _publicInterface;
    public string TailscaleInterface => _tailscaleInterface;

    public async Task RebuildAsync(IEnumerable<RelayRouteDefinition> definitions, CancellationToken ct = default)
    {
        var routes = definitions.ToArray();
        Validate(routes);
        await _gate.WaitAsync(ct);
        try
        {
            var forwardAnchor = await EnsureInfrastructureAsync(ct);
            await RunAsync(new[] { "-t", "nat", "-F", DnatChain }, true, ct);
            await RunAsync(new[] { "-t", "nat", "-F", SnatChain }, true, ct);
            await RunAsync(new[] { "-F", ForwardChain }, true, ct);

            foreach (var rule in BuildRules(routes))
                await AppendRuleAsync(rule, ct);

            _logger.LogInformation("Applied {Count} public relay route sets using {PublicInterface} -> {TailscaleInterface} (forward anchor {Anchor})",
                routes.Length, _publicInterface, _tailscaleInterface, forwardAnchor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureAsync(IEnumerable<RelayRouteDefinition> definitions, CancellationToken ct = default)
    {
        var routes = definitions.ToArray();
        Validate(routes);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureInfrastructureAsync(ct);
            foreach (var rule in BuildRules(routes))
                await EnsureRuleAsync(rule, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IpForwardingEnabled()
    {
        try
        {
            return !OperatingSystem.IsLinux() || File.ReadAllText("/proc/sys/net/ipv4/ip_forward").Trim() == "1";
        }
        catch { return false; }
    }

    private void Validate(RelayRouteDefinition[] routes)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("GameServerControl Relay requires Linux.");
        if (string.IsNullOrWhiteSpace(_publicInterface))
            throw new InvalidOperationException("Relay:PublicInterface is required.");
        if (string.IsNullOrWhiteSpace(_tailscaleInterface))
            throw new InvalidOperationException("Relay:TailscaleInterface is required.");

        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.ServerId)) throw new ArgumentException("ServerId is required.");
            if (!IPAddress.TryParse(route.TargetAddress, out var target) || target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException($"Invalid IPv4 relay target '{route.TargetAddress}'.");
            if (!_allowNonTailscaleTargets && !IsTailscaleAddress(target))
                throw new ArgumentException($"Relay target {route.TargetAddress} is outside Tailscale CGNAT range 100.64.0.0/10.");

            foreach (var port in route.Ports)
            {
                ValidateRange(port.PublicPort, port.PublicEndPort, "public", route.ServerId, port.Name);
                ValidateRange(port.TargetPort, port.TargetEndPort, "target", route.ServerId, port.Name);
                var publicWidth = (port.PublicEndPort ?? port.PublicPort) - port.PublicPort;
                var targetWidth = (port.TargetEndPort ?? port.TargetPort) - port.TargetPort;
                if (publicWidth != targetWidth)
                    throw new ArgumentException($"Relay route {route.ServerId}/{port.Name} must map ranges of equal size.");
            }
        }

        var flattened = routes.SelectMany(r => r.Ports.Select(p => (r.ServerId, Port: p))).ToArray();
        for (var i = 0; i < flattened.Length; i++)
        for (var j = i + 1; j < flattened.Length; j++)
        {
            var a = flattened[i];
            var b = flattened[j];
            if (a.Port.Protocol != b.Port.Protocol || !RangesOverlap(a.Port.PublicPort, a.Port.PublicEndPort, b.Port.PublicPort, b.Port.PublicEndPort)) continue;
            throw new ArgumentException($"Public relay port conflict: {a.ServerId}/{a.Port.Name} overlaps {b.ServerId}/{b.Port.Name}.");
        }
    }

    private static void ValidateRange(int start, int? endRaw, string kind, string serverId, string name)
    {
        var end = endRaw ?? start;
        if (start is < 1 or > 65535 || end is < 1 or > 65535 || end < start)
            throw new ArgumentException($"Invalid {kind} port range for {serverId}/{name}: {start}-{end}.");
    }

    private static bool RangesOverlap(int aStart, int? aEndRaw, int bStart, int? bEndRaw)
    {
        var aEnd = aEndRaw ?? aStart;
        var bEnd = bEndRaw ?? bStart;
        return aStart <= bEnd && bStart <= aEnd;
    }

    private static bool IsTailscaleAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private IEnumerable<Rule> BuildRules(IEnumerable<RelayRouteDefinition> routes)
    {
        yield return new Rule(null, ForwardChain,
            new[] { "-i", _tailscaleInterface, "-o", _publicInterface, "-m", "conntrack", "--ctstate", "ESTABLISHED,RELATED", "-j", "ACCEPT" });

        foreach (var route in routes.OrderBy(r => r.ServerId, StringComparer.OrdinalIgnoreCase))
        foreach (var port in route.Ports.OrderBy(p => p.Protocol).ThenBy(p => p.PublicPort))
        {
            var protocol = port.Protocol == NetworkProtocol.Tcp ? "tcp" : "udp";
            var publicSpec = PortSpec(port.PublicPort, port.PublicEndPort);
            var targetSpec = PortSpec(port.TargetPort, port.TargetEndPort);
            var targetDestination = port.TargetEndPort is null || port.TargetEndPort == port.TargetPort
                ? $"{route.TargetAddress}:{port.TargetPort}"
                : $"{route.TargetAddress}:{port.TargetPort}-{port.TargetEndPort}";
            var comment = $"gsc:{route.ServerId}:{port.Name}";
            if (comment.Length > 180) comment = comment[..180];

            yield return new Rule("nat", DnatChain,
                new[] { "-i", _publicInterface, "-p", protocol, "--dport", publicSpec, "-m", "comment", "--comment", comment, "-j", "DNAT", "--to-destination", targetDestination });
            yield return new Rule("nat", SnatChain,
                new[] { "-o", _tailscaleInterface, "-p", protocol, "-d", route.TargetAddress, "--dport", targetSpec, "-m", "comment", "--comment", comment, "-j", "MASQUERADE" });
            yield return new Rule(null, ForwardChain,
                new[] { "-i", _publicInterface, "-o", _tailscaleInterface, "-p", protocol, "-d", route.TargetAddress, "--dport", targetSpec, "-m", "conntrack", "--ctstate", "NEW,ESTABLISHED,RELATED", "-m", "comment", "--comment", comment, "-j", "ACCEPT" });
        }
    }

    private static string PortSpec(int start, int? endRaw)
    {
        var end = endRaw ?? start;
        return end == start ? start.ToString() : $"{start}:{end}";
    }

    private async Task<string> EnsureInfrastructureAsync(CancellationToken ct)
    {
        await EnsureChainAsync("nat", DnatChain, ct);
        await EnsureChainAsync("nat", SnatChain, ct);
        await EnsureChainAsync(null, ForwardChain, ct);

        await EnsureJumpAsync("nat", "PREROUTING", DnatChain, ct);
        await EnsureJumpAsync("nat", "POSTROUTING", SnatChain, ct);

        var ufwExists = await CheckAsync(new[] { "-S", "ufw-before-forward" }, ct);
        var forwardAnchor = ufwExists ? "ufw-before-forward" : "FORWARD";
        await EnsureJumpAsync(null, forwardAnchor, ForwardChain, ct);
        return forwardAnchor;
    }

    private async Task EnsureChainAsync(string? table, string chain, CancellationToken ct)
    {
        var args = new List<string>();
        if (table is not null) args.AddRange(new[] { "-t", table });
        args.AddRange(new[] { "-S", chain });
        if (await CheckAsync(args, ct)) return;

        args.Clear();
        if (table is not null) args.AddRange(new[] { "-t", table });
        args.AddRange(new[] { "-N", chain });
        await RunAsync(args, true, ct);
    }

    private async Task EnsureJumpAsync(string? table, string anchor, string target, CancellationToken ct)
    {
        var check = new List<string>();
        if (table is not null) check.AddRange(new[] { "-t", table });
        check.AddRange(new[] { "-C", anchor, "-j", target });
        if (await CheckAsync(check, ct)) return;

        var add = new List<string>();
        if (table is not null) add.AddRange(new[] { "-t", table });
        add.AddRange(new[] { "-I", anchor, "1", "-j", target });
        await RunAsync(add, true, ct);
    }

    private async Task AppendRuleAsync(Rule rule, CancellationToken ct)
    {
        var args = RuleCommand(rule, "-A");
        await RunAsync(args, true, ct);
    }

    private async Task EnsureRuleAsync(Rule rule, CancellationToken ct)
    {
        var check = RuleCommand(rule, "-C");
        if (await CheckAsync(check, ct)) return;
        await RunAsync(RuleCommand(rule, "-A"), true, ct);
    }

    private static List<string> RuleCommand(Rule rule, string action)
    {
        var args = new List<string>();
        if (rule.Table is not null) args.AddRange(new[] { "-t", rule.Table });
        args.Add(action);
        args.Add(rule.Chain);
        args.AddRange(rule.Args);
        return args;
    }

    private async Task<bool> CheckAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await RunAsync(args, false, ct);
        return result.ExitCode == 0;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> args, bool throwOnError, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _iptables,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {_iptables}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = ((await stdout) + (await stderr)).Trim();
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"iptables exited {process.ExitCode}: {output}");
        return (process.ExitCode, output);
    }
}
