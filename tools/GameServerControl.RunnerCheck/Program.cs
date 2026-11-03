using System.Reflection;
using System.Text.Json;
using GameServerControl.Shared;

var errors = new List<string>();
var shared = typeof(ServerDef).Assembly;

Type RequireType(string fullName)
{
    var type = shared.GetType(fullName);
    if (type is null)
    {
        errors.Add($"ARCH-01: missing {fullName}.");
        return typeof(object);
    }
    return type;
}

void Check(bool condition, string message)
{
    if (!condition) errors.Add(message);
}

var identityType = RequireType("GameServerControl.Shared.RunnerIdentity");
var identityInfoType = RequireType("GameServerControl.Shared.RunnerIdentityInfo");
var manifestType = RequireType("GameServerControl.Shared.RunnerManifest");
var commandType = RequireType("GameServerControl.Shared.RunnerCommand");
var requestType = RequireType("GameServerControl.Shared.RunnerRequest");
var responseType = RequireType("GameServerControl.Shared.RunnerResponse");
var snapshotType = RequireType("GameServerControl.Shared.RunnerConsoleSnapshot");

if (identityType != typeof(object) && identityInfoType != typeof(object))
{
    var forServer = identityType.GetMethod("ForServer", BindingFlags.Public | BindingFlags.Static);
    Check(forServer is not null && forServer.GetParameters() is [{ ParameterType: var p }] && p == typeof(string),
        "ARCH-01: RunnerIdentity.ForServer(string) is missing.");
    if (forServer is not null)
    {
        object? Invoke(string id) => forServer.Invoke(null, [id]);
        string Read(object value, string name) => (string)(value.GetType().GetProperty(name)?.GetValue(value)
            ?? throw new InvalidOperationException($"Missing identity property {name}"));

        var alpha = Invoke("alpha");
        var alphaAgain = Invoke("alpha");
        var beta = Invoke("beta");
        var hostile = Invoke("../../evil");
        if (alpha is not null && alphaAgain is not null && beta is not null && hostile is not null)
        {
            Check(Read(alpha, "Key") == "8ed3f6ad685b959e", "ARCH-01: alpha identity key changed unexpectedly.");
            Check(Read(alpha, "ServiceName") == "GameServerControlRunner_8ed3f6ad685b959e", "ARCH-01: service name must be deterministic and hash-derived.");
            Check(Read(alpha, "PipeName") == "gsc-runner-8ed3f6ad685b959e", "ARCH-01: pipe name must be deterministic and hash-derived.");
            Check(Read(alpha, "StateDirectoryName") == "8ed3f6ad685b959e", "ARCH-01: state directory must be deterministic and hash-derived.");
            Check(Read(alpha, "ServiceName") == Read(alphaAgain, "ServiceName"), "ARCH-01: same server ID must produce the same service identity.");
            Check(Read(alpha, "ServiceName") != Read(beta, "ServiceName"), "ARCH-01: different server IDs must not share a service identity.");
            var hostileService = Read(hostile, "ServiceName");
            var hostilePipe = Read(hostile, "PipeName");
            Check(!hostileService.Contains("..") && !hostileService.Contains('/') && !hostileService.Contains('\\'), "ARCH-01: raw hostile server ID leaked into service name.");
            Check(!hostilePipe.Contains("..") && !hostilePipe.Contains('/') && !hostilePipe.Contains('\\'), "ARCH-01: raw hostile server ID leaked into pipe name.");
        }
    }
}

if (manifestType != typeof(object))
{
    var ctor = manifestType.GetConstructors().SingleOrDefault();
    var validate = manifestType.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance);
    Check(ctor is not null, "ARCH-01: RunnerManifest primary constructor is missing.");
    Check(validate is not null && validate.GetParameters().Length == 0 && validate.ReturnType == typeof(string),
        "ARCH-01: RunnerManifest.Validate() must return string?.");
    if (ctor is not null && validate is not null)
    {
        object NewManifest(string exe, string working) => ctor.Invoke([
            "alpha",
            exe,
            working,
            new[] { "-port", "2456" },
            false,
            "896660",
            new[] { "helper.exe" },
            false
        ]);

        var good = NewManifest(@"C:\Games\Alpha\server.exe", @"C:\Games\Alpha");
        var badExe = NewManifest(@"Games\Alpha\server.exe", @"C:\Games\Alpha");
        var badWorking = NewManifest(@"C:\Games\Alpha\server.exe", @"Games\Alpha");
        Check(validate.Invoke(good, null) is null, "ARCH-01: rooted Windows manifest paths should validate.");
        Check(validate.Invoke(badExe, null) is string, "ARCH-01: relative executable path must be rejected.");
        Check(validate.Invoke(badWorking, null) is string, "ARCH-01: relative working directory must be rejected.");
    }
}

if (requestType != typeof(object) && commandType != typeof(object))
{
    var properties = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(x => x).ToArray();
    var expected = new[] { "AfterSequence", "Command", "ConsoleInput", "Force" }.OrderBy(x => x).ToArray();
    Check(properties.SequenceEqual(expected), "ARCH-01: RunnerRequest may carry only command controls plus the bounded console sequence cursor.");

    var ctor = requestType.GetConstructors().SingleOrDefault();
    var validate = requestType.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance);
    Check(ctor is not null, "ARCH-01: RunnerRequest primary constructor is missing.");
    Check(validate is not null && validate.GetParameters().Length == 0 && validate.ReturnType == typeof(string),
        "ARCH-01: RunnerRequest.Validate() must return string?.");
    if (ctor is not null && validate is not null)
    {
        object Cmd(string name, bool force = false, string? input = null, long? afterSequence = null)
        {
            var command = Enum.Parse(commandType, name);
            return ctor.Invoke([command, force, input, afterSequence]);
        }

        var ping = Cmd("Ping");
        var validInput = Cmd("SendConsoleInput", input: "save");
        var newlineInput = Cmd("SendConsoleInput", input: "save\nquit");
        var oversizedInput = Cmd("SendConsoleInput", input: new string('x', 4097));
        var validCursor = Cmd("ConsoleState", afterSequence: 12);
        var negativeCursor = Cmd("ConsoleState", afterSequence: -1);
        var wrongCursorCommand = Cmd("Start", afterSequence: 12);
        Check(validate.Invoke(ping, null) is null, "ARCH-01: Ping request should validate.");
        Check(validate.Invoke(validInput, null) is null, "ARCH-01: single bounded console command should validate.");
        Check(validate.Invoke(newlineInput, null) is string, "ARCH-01: console input containing newline must be rejected.");
        Check(validate.Invoke(oversizedInput, null) is string, "ARCH-01: console input over 4096 characters must be rejected.");
        Check(validate.Invoke(validCursor, null) is null, "ARCH-01: non-negative ConsoleState cursor should validate.");
        Check(validate.Invoke(negativeCursor, null) is string, "ARCH-01: negative ConsoleState cursor must be rejected.");
        Check(validate.Invoke(wrongCursorCommand, null) is string, "ARCH-01: console cursor must not be accepted for non-ConsoleState commands.");

        var json = JsonSerializer.Serialize(ping, requestType);
        Check(!json.Contains("Executable", StringComparison.OrdinalIgnoreCase)
              && !json.Contains("WorkingDirectory", StringComparison.OrdinalIgnoreCase)
              && !json.Contains("Arguments", StringComparison.OrdinalIgnoreCase),
            "ARCH-01: serialized RunnerRequest exposed launch metadata.");
    }
}

Check(responseType != typeof(object), "ARCH-01: RunnerResponse is required.");
Check(snapshotType != typeof(object), "ARCH-01: RunnerConsoleSnapshot is required.");

if (errors.Count > 0)
{
    foreach (var error in errors.Distinct()) Console.Error.WriteLine(error);
    Environment.Exit(1);
}

Console.WriteLine("ARCH-01 Runner protocol behavior OK.");
