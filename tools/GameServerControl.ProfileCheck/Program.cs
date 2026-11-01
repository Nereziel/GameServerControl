using GameServerControl.Shared;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

GameProfile RequireProfile(string key)
{
    var profile = GameProfiles.FindByKey(key);
    if (profile is not null) return profile;
    failures.Add($"Missing game profile: {key}");
    return GameProfiles.All[0];
}

NetworkPort? Port(GameProfile profile, string name) =>
    profile.DefaultPorts?.FirstOrDefault(p =>
        p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

ServerDef Server(string appId, string[] args, NetworkPort[] ports) => new(
    Id: "contract-check",
    Name: "Contract check",
    VmName: "",
    GameType: GameType.SteamGeneric,
    GuestExePath: "/srv/gameservers/test/server",
    GuestWorkingDir: "/srv/gameservers/test",
    StartArgs: args,
    SaveDirs: Array.Empty<string>(),
    SteamAppId: appId,
    GuestCredentialId: null,
    LogPathInGuest: null,
    HostingMode: HostingMode.BareMetal,
    NetworkPorts: ports);

var valheim = RequireProfile("valheim");
Check(valheim.SteamAppId == "896660", "Valheim Steam App ID must remain 896660.");
Check(valheim.Installable, "Valheim must remain installable.");
Check(valheim.Capabilities.ManagedConfig, "Valheim must retain managed configuration.");
Check(valheim.Capabilities.LiveLog, "Valheim must retain live log support.");
Check(valheim.Capabilities.Mods, "Valheim must retain mod support.");
Check(!valheim.Capabilities.HyperV, "Valheim curated profile must remain BareMetal-only.");
Check(valheim.ExecutableRelative == "valheim_server.exe", "Valheim Windows executable changed unexpectedly.");
Check(valheim.LinuxExecutableRelative == "valheim_server.x86_64", "Valheim Linux executable changed unexpectedly.");
var valheimGame = Port(valheim, "Game");
Check(valheimGame is not null, "Valheim Game port reservation is missing.");
if (valheimGame is not null)
{
    Check(valheimGame.Protocol == NetworkProtocol.Udp, "Valheim Game ports must use UDP.");
    Check(valheimGame.Port == 2456 && valheimGame.EndPort == 2457,
        "Valheim default reservation must remain UDP 2456-2457.");
}
Check(valheim.DefaultStartArgs.Select((x, i) => (x, i)).Any(x =>
        x.x.Equals("-port", StringComparison.OrdinalIgnoreCase) &&
        x.i + 1 < valheim.DefaultStartArgs.Length && valheim.DefaultStartArgs[x.i + 1] == "2456"),
    "Valheim Windows launch args must contain -port 2456.");
Check(valheim.LinuxDefaultStartArgs?.Select((x, i) => (x, i)).Any(x =>
        x.x.Equals("-port", StringComparison.OrdinalIgnoreCase) &&
        x.i + 1 < valheim.LinuxDefaultStartArgs.Length && valheim.LinuxDefaultStartArgs[x.i + 1] == "2456") == true,
    "Valheim Linux launch args must contain -port 2456.");
var valheimPortField = ConfigSchemas.Valheim.AllFields.FirstOrDefault(f => f.Key == "port");
Check(valheimPortField?.Min == 1024 && valheimPortField?.Max == 65534,
    "Valheim managed port field must remain 1024-65534 because port+1 is reserved.");
var legacyValheim = CuratedNetwork.Normalize(Server("896660",
    new[] { "-name", "Legacy", "-port", "2600" },
    new[] { new NetworkPort("Game", NetworkProtocol.Udp, 2456, 2457) }));
Check(legacyValheim.NetworkPorts is { Length: 1 } &&
      legacyValheim.NetworkPorts[0].Port == 2600 && legacyValheim.NetworkPorts[0].EndPort == 2601,
    "Valheim legacy reservation must normalize from the actual -port launch argument.");

var dragonwilds = RequireProfile("dragonwilds");
Check(dragonwilds.SteamAppId == "4019830", "Dragonwilds Steam App ID must remain 4019830.");
Check(dragonwilds.Installable, "Dragonwilds must remain installable.");
Check(dragonwilds.Capabilities.ManagedConfig, "Dragonwilds must retain managed configuration.");
Check(dragonwilds.Capabilities.LiveLog, "Dragonwilds must retain live log support.");
Check(!dragonwilds.Capabilities.HyperV, "Dragonwilds curated profile must remain BareMetal-only.");
Check(dragonwilds.ExecutableRelative == "RSDragonwilds.exe", "Dragonwilds Windows executable changed unexpectedly.");
Check(dragonwilds.LinuxExecutableRelative == "RSDragonwildsServer.sh", "Dragonwilds Linux executable changed unexpectedly.");
var dragonGame = Port(dragonwilds, "Game");
var dragonBeacon = Port(dragonwilds, "Beacon");
Check(dragonGame is not null && dragonGame.Protocol == NetworkProtocol.Udp && dragonGame.Port == 7777,
    "Dragonwilds Game port must default to UDP 7777.");
Check(dragonBeacon is not null && dragonBeacon.Protocol == NetworkProtocol.Udp && dragonBeacon.Port == 8888,
    "Dragonwilds Beacon port must default to UDP 8888.");
Check(dragonwilds.DefaultStartArgs.Contains("-Port=7777"),
    "Dragonwilds Windows launch args must contain -Port=7777.");
Check(dragonwilds.DefaultStartArgs.Contains("-BeaconPort=8888"),
    "Dragonwilds Windows launch args must contain -BeaconPort=8888.");
Check(dragonwilds.LinuxDefaultStartArgs?.Contains("-Port=7777") == true,
    "Dragonwilds Linux launch args must contain -Port=7777.");
Check(dragonwilds.LinuxDefaultStartArgs?.Contains("-BeaconPort=8888") == true,
    "Dragonwilds Linux launch args must contain -BeaconPort=8888.");
var legacyDragonwilds = CuratedNetwork.Normalize(Server("4019830",
    new[] { "-log", "-Port=9000", "-BeaconPort=9010" },
    new[]
    {
        new NetworkPort("Game", NetworkProtocol.Udp, 7777),
        new NetworkPort("Beacon", NetworkProtocol.Udp, 8888)
    }));
Check(legacyDragonwilds.NetworkPorts is { Length: 2 } &&
      legacyDragonwilds.NetworkPorts.Any(p => p.Name == "Game" && p.Port == 9000) &&
      legacyDragonwilds.NetworkPorts.Any(p => p.Name == "Beacon" && p.Port == 9010),
    "Dragonwilds legacy reservations must normalize from -Port/-BeaconPort launch arguments.");

if (failures.Count == 0)
{
    Console.WriteLine("Curated profile contracts OK: Valheim + Dragonwilds.");
    return 0;
}

Console.Error.WriteLine("Curated profile contract failures:");
foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
return 1;
