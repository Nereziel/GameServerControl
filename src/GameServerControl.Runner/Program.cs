using System.Text.Json;
using GameServerControl.Shared;
using GameServerControl.Runner;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("GameServerControl.Runner is Windows-only.");

var manifestPath = ResolveManifestPath(args);
if (!Path.IsPathFullyQualified(manifestPath))
    throw new InvalidOperationException("--manifest must be an absolute path.");

var json = await File.ReadAllTextAsync(manifestPath);
var manifest = JsonSerializer.Deserialize<RunnerManifest>(json,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Runner manifest is empty or invalid JSON.");
var manifestError = manifest.Validate();
if (manifestError is not null)
    throw new InvalidOperationException("Invalid Runner manifest: " + manifestError);

var identity = RunnerIdentity.ForServer(manifest.ServerId);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = identity.ServiceName);
builder.Services.AddSingleton(manifest);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton<RunnerProcessHost>();
builder.Services.AddSingleton<RunnerPipeServer>();
builder.Services.AddHostedService<RunnerWorker>();

await builder.Build().RunAsync();

static string ResolveManifestPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--manifest", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    throw new InvalidOperationException("Runner requires --manifest <absolute-path>.");
}
