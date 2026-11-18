using System.Net;
using System.Net.NetworkInformation;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Networking;

public sealed record NetworkPortDiagnostic(string Name, NetworkProtocol Protocol, int Port, string State);
public sealed record HostNetworkDiagnostic(bool Supported, string Note, IReadOnlyList<NetworkPortDiagnostic> Ports);

/// <summary>
/// Passive inspection of local listening sockets. This does not establish public
/// reachability, firewall acceptance, game protocol readiness, or socket ownership.
/// </summary>
public static class ServerNetworkDiagnostics
{
    public static HostNetworkDiagnostic Check(ServerDef def)
    {
        if (def.HostingMode != HostingMode.BareMetal)
            return new HostNetworkDiagnostic(false,
                "Hyper-V guest sockets cannot be inspected from the host Agent.", []);

        try
        {
            var network = IPGlobalProperties.GetIPGlobalProperties();
            return Inspect(def, network.GetActiveTcpListeners(), network.GetActiveUdpListeners());
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException
                                   or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new HostNetworkDiagnostic(false, "The host could not enumerate local listening sockets.", []);
        }
    }

    // Injected listeners make the decision testable without opening a port.
    public static HostNetworkDiagnostic Inspect(ServerDef def, IPEndPoint[] tcpListeners, IPEndPoint[] udpListeners)
    {
        if (def.HostingMode != HostingMode.BareMetal)
            return new HostNetworkDiagnostic(false,
                "Hyper-V guest sockets cannot be inspected from the host Agent.", []);

        var ports = def.NetworkPorts ?? GameProfiles.FindFor(def)?.DefaultPorts ?? [];
        var results = new List<NetworkPortDiagnostic>();
        foreach (var entry in ports)
        {
            var end = entry.EndPort ?? entry.Port;
            if (entry.Port < 1 || end > 65535 || end < entry.Port) continue;
            if (end - entry.Port >= 32)
            {
                results.Add(new NetworkPortDiagnostic(entry.Name, entry.Protocol, entry.Port, "RangeTooLarge"));
                continue;
            }
            var listeners = entry.Protocol == NetworkProtocol.Tcp ? tcpListeners : udpListeners;
            for (var port = entry.Port; port <= end; port++)
            {
                var matching = listeners.Where(ep => ep.Port == port).ToArray();
                var state = matching.Length == 0 ? "NotListening"
                    : matching.Any(ep => IPAddress.Any.Equals(ep.Address) || IPAddress.IPv6Any.Equals(ep.Address))
                        ? "ListeningAny"
                    : matching.All(ep => IPAddress.IsLoopback(ep.Address)) ? "LoopbackOnly"
                    : "InterfaceBound";
                results.Add(new NetworkPortDiagnostic(entry.Name, entry.Protocol, port, state));
            }
        }
        return new HostNetworkDiagnostic(true,
            "Local socket presence only. A listening socket may belong to another process. Firewall, VPS forwarding and game handshake are not verified.",
            results);
    }
}
