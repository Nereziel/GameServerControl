namespace GameServerControl.Shared;

/// <summary>
/// Optional public-port override for a server exposed through a GameServerControl Relay.
/// Target ports are always derived from the server's effective NetworkPorts so game config,
/// collision detection, local firewall metadata and relay routing cannot silently drift apart.
/// </summary>
public record RelayPublicPort(
    string Name,
    NetworkProtocol Protocol,
    int Port,
    int? EndPort = null);

/// <summary>
/// Per-server public relay settings. The optional Agent-level fields are accepted by the
/// existing server relay endpoint so the Web UI can configure the Relay connection without
/// exposing a second unauthenticated configuration surface. RelayApiToken is write-only:
/// status responses only expose whether a token is configured.
/// </summary>
public record PublicRelayServerSettings(
    bool Enabled,
    RelayPublicPort[]? PublicPorts = null,
    string? RelayUrl = null,
    string? RelayApiToken = null,
    string? TargetAddress = null,
    string? PublicAddress = null);

public record RelayRoutePort(
    string Name,
    NetworkProtocol Protocol,
    int PublicPort,
    int? PublicEndPort,
    int TargetPort,
    int? TargetEndPort);

public record RelayRouteDefinition(
    string ServerId,
    string TargetAddress,
    RelayRoutePort[] Ports);

public record PublicRelayServerStatus(
    bool Configured,
    bool Enabled,
    bool Reachable,
    string? PublicAddress,
    string? TargetAddress,
    RelayRoutePort[] Ports,
    string? Error = null,
    string? RelayUrl = null,
    bool ApiTokenConfigured = false,
    string? ConfiguredTargetAddress = null);
