using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Config;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Logs;
using GameServerControl.Agent.Networking;
using GameServerControl.Agent.MultiAgent;
using GameServerControl.Agent.Notifications;
using GameServerControl.Agent.Rcon;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

// The Windows Task Scheduler invokes this narrowly-scoped command as SYSTEM.
// Do not run a second web server; load the protected Agent credential only in
// this transient process (never include it in the scheduled task definition).
if (args.Length == 3 && args[0] == "--scheduled-maintenance")
{
    Environment.ExitCode = await ScheduledMaintenanceCommand.RunAsync(args[1], args[2]);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Host.UseWindowsService();

var bind = builder.Configuration["Agent:Bind"] ?? "http://127.0.0.1:5099";
var bindHttps = builder.Configuration["Agent:BindHttps"];
var bindUrls = new List<string> { bind };
if (!string.IsNullOrWhiteSpace(bindHttps)) bindUrls.Add(bindHttps);

// Keep loopback WebUI available while giving the VPS gateway a dedicated
// Tailscale-only listener. Never bind a second administrative listener to
// 0.0.0.0 or an arbitrary public interface.
var bindTailscale = builder.Configuration["Agent:BindTailscale"];
if (!string.IsNullOrWhiteSpace(bindTailscale))
{
    if (!Uri.TryCreate(bindTailscale, UriKind.Absolute, out var vpnUri) ||
        vpnUri.Scheme != Uri.UriSchemeHttp || vpnUri.AbsolutePath != "/" ||
        vpnUri.Query.Length != 0 || vpnUri.Fragment.Length != 0 ||
        !string.IsNullOrEmpty(vpnUri.UserInfo) || vpnUri.Port != 5099 ||
        !System.Net.IPAddress.TryParse(vpnUri.Host, out var vpnIp) ||
        vpnIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new InvalidOperationException("Agent:BindTailscale must be http://<Tailscale IPv4>:5099.");
    var bytes = vpnIp.GetAddressBytes();
    if (bytes[0] != 100 || bytes[1] is < 64 or > 127)
        throw new InvalidOperationException("Agent:BindTailscale must use Tailscale's 100.64.0.0/10 address range.");
    bindUrls.Add(bindTailscale.TrimEnd('/'));
}
builder.WebHost.UseUrls(bindUrls.ToArray());

if (!string.IsNullOrWhiteSpace(bindHttps))
{
    builder.WebHost.ConfigureKestrel(opts =>
    {
        var pfxPath = Path.Combine(AppContext.BaseDirectory, "agent.pfx");
        var pfxPassword = builder.Configuration["Agent:CertPassword"] ?? "gscagent";
        var sanHosts = new List<string> { Environment.MachineName.ToLowerInvariant() };
        foreach (var host in (builder.Configuration["Agent:CertSanHosts"] ?? "")
                     .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!sanHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) sanHosts.Add(host);

        var sanIps = new List<System.Net.IPAddress>();
        foreach (var text in (builder.Configuration["Agent:CertSanIps"] ?? "")
                     .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (System.Net.IPAddress.TryParse(text, out var ip)) sanIps.Add(ip);

        var cert = CertHelper.LoadOrCreate(pfxPath, pfxPassword, sanHosts.ToArray(), sanIps.ToArray());
        opts.ConfigureHttpsDefaults(o => o.ServerCertificate = cert);
    });
}

builder.Services.AddSingleton<PowerShellRunner>();
builder.Services.AddSingleton<HypervService>();
builder.Services.AddSingleton<GuestProcessService>();
builder.Services.AddSingleton<WindowsRunnerPipeClient>();
builder.Services.AddSingleton<WindowsRunnerServiceManager>();
builder.Services.AddSingleton<IWindowsRunnerServiceManager>(sp => sp.GetRequiredService<WindowsRunnerServiceManager>());
builder.Services.AddSingleton<LocalProcessService>();
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddHttpClient("managed-hosts", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(7)
    });
builder.Services.AddSingleton<ManagedHostStore>();
builder.Services.AddSingleton<ManagedHostGateway>();
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<ServerStatusPoller>();
builder.Services.AddSingleton<PublicRelayManager>();
builder.Services.AddSingleton<StatusTracker>();
builder.Services.AddSingleton<ServerUpdateConsole>();
builder.Services.AddSingleton<ServerDiagnostics>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var dataRoot = cfg["Agent:DataDir"] ?? env.ContentRootPath;
    if (!Path.IsPathRooted(dataRoot)) dataRoot = Path.Combine(env.ContentRootPath, dataRoot);
    return new ServerDiagnostics(Path.Combine(dataRoot, "diagnostics"),
        sp.GetRequiredService<ILogger<ServerDiagnostics>>());
});
builder.Services.AddSingleton<ServerAlertPolicy>();
builder.Services.AddSingleton<ServerLifecycleGate>();
builder.Services.AddSingleton<ServerOrchestrator>();
builder.Services.AddSingleton<ServerDeletionService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<MaintenanceScheduler>();
builder.Services.AddSingleton<SteamCmdManager>();
builder.Services.AddSingleton<ServerInstaller>();
builder.Services.AddSingleton<MinecraftServerPackages>();
builder.Services.AddHttpClient("minecraft-installer", client =>
{
    client.Timeout = TimeSpan.FromMinutes(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.1 (+https://github.com/Nereziel/GameServerControl)");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseProxy = false
});
builder.Services.AddSingleton<DiscordNotifier>();
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddSingleton<AgentUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());
builder.Services.AddSingleton<ValheimConfig>();
builder.Services.AddSingleton<PalworldConfig>();
builder.Services.AddSingleton<WindroseConfig>();
builder.Services.AddSingleton<SatisfactoryConfig>();
builder.Services.AddSingleton<DragonwildsConfig>();
builder.Services.AddSingleton<GameServerControl.Agent.Admin.SatisfactoryAdminClient>();
builder.Services.AddSingleton<IDynamicSchemaExtension, PalworldDynamicSchema>();
builder.Services.AddSingleton<IDynamicSchemaExtension, SatisfactoryDynamicSchema>();
builder.Services.AddSingleton<IDynamicSchemaExtension, DragonwildsDynamicSchema>();
builder.Services.AddSingleton<GameServerControl.Agent.Discovery.SteamLibraryReader>();
builder.Services.AddSingleton<GameServerControl.Agent.Discovery.ServerDiscoveryService>();
builder.Services.AddSingleton<GameConfigFactory>();
builder.Services.AddSingleton<ServerConfigFileService>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.ThunderstoreClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.FicsitClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.GitHubReleasesClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.ValheimBepInExModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.SatisfactoryThunderstoreModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.PalworldCuratedModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.ArkWorkshopModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.MinecraftJavaModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.MinecraftBedrockAddOnManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager>(
    sp => sp.GetRequiredService<GameServerControl.Agent.Mods.MinecraftBedrockAddOnManager>());
builder.Services.AddHttpClient("minecraft-modding", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.1 (+https://github.com/Nereziel/GameServerControl)");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseProxy = false
});
builder.Services.AddSingleton<GameServerControl.Agent.Mods.ModManagerRegistry>();
builder.Services.AddSingleton<SourceRconClient>();
builder.Services.AddSingleton<IGameRcon, PalworldRcon>();
builder.Services.AddSingleton<RconService>();
builder.Services.AddSingleton<LogTailService>();

{
    using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var startupLogger = startupLoggerFactory.CreateLogger("FirstRunTokenGenerator");
    if (FirstRunTokenGenerator.EnsureToken(builder.Configuration, AppContext.BaseDirectory, startupLogger, out var resolvedToken))
        builder.Configuration["Agent:ApiToken"] = resolvedToken;
}

builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<TokenRegistry>();
builder.Services.AddSingleton<BrowserSessionStore>();

builder.Services.AddAuthentication(TokenAuthHandler.SchemeName)
    .AddScheme<TokenAuthOptions, TokenAuthHandler>(TokenAuthHandler.SchemeName, opts =>
    {
        opts.Token = builder.Configuration["Agent:ApiToken"] ?? "";
    });
builder.Services.AddAuthorization(o =>
{
    o.DefaultPolicy = new AuthorizationPolicyBuilder(TokenAuthHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
    o.AddPolicy("admin", p => p
        .RequireAuthenticatedUser()
        .RequireClaim(TokenAuthHandler.RoleClaim, "Admin"));
});

builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

var webUiDirectory = Path.Combine(AppContext.BaseDirectory, "WebUi");
// Fingerprint the deployed asset bytes, not the assembly version. A rebuilt or
// updated WebUI always gets a new URL, even when the app version is unchanged.
using var webUiHasher = System.Security.Cryptography.IncrementalHash.CreateHash(
    System.Security.Cryptography.HashAlgorithmName.SHA256);
foreach (var asset in new[] { "styles.css", "core.js", "server-admin.js", "jobs-mods.js", "config-backups.js", "server-workspace.js" })
{
    var assetPath = Path.Combine(webUiDirectory, asset);
    if (File.Exists(assetPath))
        webUiHasher.AppendData(File.ReadAllBytes(assetPath));
}
var webUiAssetVersion = Convert.ToHexString(webUiHasher.GetHashAndReset())[..16].ToLowerInvariant();

app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var htmlPath = Path.Combine(webUiDirectory, "index.html");
    return File.Exists(htmlPath)
        ? Results.Content(
            File.ReadAllText(htmlPath).Replace("__GSC_ASSET_VERSION__", webUiAssetVersion),
            "text/html; charset=utf-8")
        : Results.NotFound("WebUi/index.html missing in publish output");
});

var webUiAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["styles.css"] = "text/css; charset=utf-8",
    ["core.js"] = "text/javascript; charset=utf-8",
    ["server-admin.js"] = "text/javascript; charset=utf-8",
    ["jobs-mods.js"] = "text/javascript; charset=utf-8",
    ["config-backups.js"] = "text/javascript; charset=utf-8",
    ["server-workspace.js"] = "text/javascript; charset=utf-8"
};
app.MapGet("/webui/{file}", (string file) =>
{
    if (!webUiAssets.TryGetValue(file, out var contentType)) return Results.NotFound();
    var path = Path.Combine(AppContext.BaseDirectory, "WebUi", file);
    return File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
});

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
// Audit must wrap role enforcement to record denied requests too.
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<RoleEnforcementMiddleware>();

// Origin-scoped browser WebCrypto keys, not persistent bearer credentials.
// All challenges are in-memory, one-time, and expire after thirty seconds.
app.MapPost("/auth/browser-key/challenge", (HttpContext ctx, LocalBrowserIdentity input,
    BrowserSessionStore sessions, TokenRegistry registry) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (!BrowserSessionAccess.Allowed(ctx)) return Results.NotFound();
    var challenge = sessions.CreateChallenge(input.Id, registry);
    return challenge is null ? Results.NoContent() : Results.Ok(new { challenge });
}).AllowAnonymous();

app.MapPost("/auth/browser-key/redeem", (HttpContext ctx, LocalBrowserProof proof,
    BrowserSessionStore sessions, TokenRegistry registry) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (!BrowserSessionAccess.Allowed(ctx)) return Results.NotFound();
    var bearer = sessions.Redeem(proof.Id, proof.Challenge, proof.Signature, registry);
    return bearer is null ? Results.Unauthorized() : Results.Ok(new { token = bearer });
}).AllowAnonymous();

app.MapPost("/auth/browser-key/forget", (HttpContext ctx, LocalBrowserIdentity input,
    BrowserSessionStore sessions) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (!BrowserSessionAccess.Allowed(ctx)) return Results.NotFound();
    sessions.Forget(input.Id);
    return Results.NoContent();
}).AllowAnonymous();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapPost("/auth/browser-key", (HttpContext ctx, LocalBrowserRegistration input,
    BrowserSessionStore sessions, TokenRegistry registry) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (!BrowserSessionAccess.Allowed(ctx)) return Results.NotFound();
    var tokenId = ctx.User.FindFirst(TokenAuthHandler.TokenIdClaim)?.Value;
    if (string.IsNullOrWhiteSpace(tokenId)) return Results.Forbid();
    var bearer = ctx.Request.Headers.Authorization.ToString();
    if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Results.Forbid();
    var presented = bearer["Bearer ".Length..].Trim();
    if (registry.Lookup(presented)?.Role != TokenRole.Admin) return Results.Forbid();
    try
    {
        var id = sessions.Register(tokenId, presented, input.PublicKey ?? "", registry);
        return Results.Ok(new { id });
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or
                                System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest(new { error = "Invalid browser signing key." });
    }
}).RequireAuthorization("admin");
api.MapServerConfigFiles();
api.MapOperationalEndpoints();
api.MapManagedHosts();

api.MapGet("/health", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));
api.MapGet("/version", (UpdateChecker checker) => Results.Ok(checker.Latest));
api.MapPost("/version/check", async (UpdateChecker checker, CancellationToken ct) =>
    Results.Ok(await checker.CheckAsync(ct)));
api.MapGet("/game-profiles", () => Results.Ok(GameProfiles.All));
api.MapGet("/capabilities", (IConfiguration cfg) => Results.Ok(new
{
    schemaVersion = 1,
    features = new[]
    {
        "minecraft.install",
        "minecraft.java.mods",
        "minecraft.bedrock.addons",
        "install.cancel",
        "install.history",
        "install.restart-recovery",
        "backup.retention.safe"
    }.Concat(string.IsNullOrWhiteSpace(cfg["Agent:PermanentDeleteRoot"])
        ? Array.Empty<string>() : new[] { "server.delete.permanent" }).ToArray()
}));
api.MapPost("/update", (AgentUpdateService updater) =>
{
    var result = updater.StartLatestUpdate();
    return result.Started ? Results.Accepted(value: result) : Results.BadRequest(result);
}).RequireAuthorization("admin");

api.MapGet("/discover", (GameServerControl.Agent.Discovery.ServerDiscoveryService disco) =>
    Results.Ok(disco.Discover()));

api.MapGet("/servers", (ServerRegistry reg) => Results.Ok(reg.All));
api.MapPost("/servers/reload", (ServerRegistry reg) =>
{
    reg.Reload();
    return Results.Ok(new { reloaded = true, count = reg.All.Count });
});
api.MapPost("/servers", (ServerDef def, ServerStore store) =>
{
    try { return Results.Ok(store.Add(def)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

api.MapPost("/servers/install", (InstallServerRequest req, ServerInstaller installer, ServerRegistry registry) =>
{
    if (string.IsNullOrWhiteSpace(req.SteamAppId) || !req.SteamAppId.All(char.IsDigit))
        return Results.BadRequest(new { error = "SteamAppId must be numeric." });
    if (string.IsNullOrWhiteSpace(req.InstallPath))
        return Results.BadRequest(new { error = "InstallPath is required." });
    if (req.ServerDef is null)
        return Results.BadRequest(new { error = "ServerDef is required." });
    if (registry.Get(req.ServerDef.Id) is not null)
        return Results.Conflict(new { error = $"Server with id '{req.ServerDef.Id}' already exists." });
    try
    {
        var jobId = installer.StartJob(req);
        return Results.Ok(new InstallJobAck(jobId, "Install job queued"));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/servers/minecraft/install", (MinecraftInstallRequest request,
    ServerInstaller installer, ServerRegistry registry) =>
{
    var validation = MinecraftServerPackages.Validate(request);
    if (validation is not null) return Results.BadRequest(new { error = validation });
    if (registry.Get(request.ServerId) is not null)
        return Results.Conflict(new { error = "A server with this ID already exists." });
    try
    {
        var jobId = installer.StartMinecraftJob(request);
        return Results.Ok(new InstallJobAck(jobId, "Minecraft installation queued"));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/servers/minecraft/recover", (MinecraftRecoveryRequest request, ServerStore store,
    ServerRegistry registry) =>
{
    if (registry.Get(request.ServerId) is not null)
        return Results.Conflict(new { error = "A server with this ID is already registered." });
    try
    {
        var definition = MinecraftServerPackages.RecoverDefinition(request);
        return Results.Ok(store.Add(definition));
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}).RequireAuthorization("admin");
api.MapPost("/servers/{id}/minecraft/bedrock/addons", async (
    string id, HttpRequest request, ServerRegistry registry,
    GameServerControl.Agent.Mods.MinecraftBedrockAddOnManager addOns, CancellationToken ct) =>
{
    var def = registry.Get(id);
    if (def is null) return Results.NotFound();
    if (!addOns.Supports(def)) return Results.BadRequest(new { error = "This server is not official Bedrock." });
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a .mcpack/.mcaddon multipart form." });
    try
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No add-on file supplied." });
        if (file.Length > 125L * 1024 * 1024)
            return Results.BadRequest(new { error = "Add-on upload exceeds 125 MiB." });
        await using var stream = file.OpenReadStream();
        var result = await addOns.ImportAsync(def, file.FileName, stream, ct);
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { error = "Unable to import Bedrock add-on: " + ex.Message });
    }
});
api.MapGet("/servers/install/jobs", (ServerInstaller installer) =>
    Results.Ok(installer.ListJobs())).RequireAuthorization("admin");
api.MapPost("/servers/install/{jobId}/cancel", (string jobId, ServerInstaller installer) =>
{
    if (installer.Get(jobId) is null) return Results.NotFound();
    return installer.CancelJob(jobId)
        ? Results.Accepted(value: new { requested = true, jobId })
        : Results.Conflict(new { error = "Install job has already finished." });
}).RequireAuthorization("admin");
api.MapGet("/servers/install/{jobId}", (string jobId, ServerInstaller installer) =>
{
    var p = installer.Get(jobId);
    return p is null ? Results.NotFound() : Results.Ok(p);
});

// Update preferences use a narrow endpoint so secrets never need round-tripping.
api.MapGet("/servers/{id}/auto-update", (string id, ServerRegistry registry) =>
{
    var def = registry.Get(id);
    return def is null ? Results.NotFound() :
        Results.Ok(new { def.AutoUpdateOnStart, def.AutoUpdateOnRestart,
            Available = def.HostingMode == HostingMode.BareMetal &&
                        !string.IsNullOrWhiteSpace(def.SteamAppId) });
});
api.MapPut("/servers/{id}/auto-update", (string id, AutoUpdatePreferences req, ServerStore store,
    ServerRegistry registry) =>
{
    var def = registry.Get(id);
    if (def is null) return Results.NotFound();
    try
    {
        var updated = store.SetAutoUpdatePreferences(id, req.AutoUpdateOnStart, req.AutoUpdateOnRestart);
        return Results.Ok(new { updated.AutoUpdateOnStart, updated.AutoUpdateOnRestart });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("admin");
api.MapGet("/servers/{id}/update/console", (string id, long? since, ServerRegistry registry,
    ServerUpdateConsole updates) =>
    registry.Get(id) is null ? Results.NotFound() : Results.Ok(updates.Snapshot(id, since)))
    .RequireAuthorization("admin");

api.MapPut("/servers/{id}", async (string id, ServerDef def, ServerStore store, ServerLifecycleGate lifecycle, CancellationToken ct) =>
    await lifecycle.RunExclusiveAsync<IResult>(id, () =>
    {
        try { return Task.FromResult<IResult>(Results.Ok(store.Update(id, def, preserveNotifications: true))); }
        catch (ArgumentException ex) { return Task.FromResult<IResult>(Results.BadRequest(new { error = ex.Message })); }
        catch (KeyNotFoundException) { return Task.FromResult<IResult>(Results.NotFound()); }
    }, () => Results.Conflict(new { error = "Another lifecycle operation is running." }), ct));
api.MapDelete("/servers/{id}", async (
    string id, ServerRegistry reg, ServerStore store, IWindowsRunnerServiceManager runners,
    PublicRelayManager relay, ServerLifecycleGate lifecycle, CancellationToken ct) =>
    await lifecycle.RunExclusiveAsync<IResult>(id, async () =>
    {
        var def = reg.Get(id);
        if (def is null) return Results.NotFound();
        var deprovisioned = false;
        try
        {
            if (runners.Supports(def))
            {
                var cleanup = await runners.DeprovisionAsync(def, ct);
                if (!cleanup.Ok)
                    return Results.Json(new { error = cleanup.Error }, statusCode: StatusCodes.Status502BadGateway);
                deprovisioned = true;
            }
            if (!store.Delete(id)) return Results.NotFound();
            await relay.RemoveServerAsync(id, CancellationToken.None);
            return Results.NoContent();
        }
        finally { if (deprovisioned) runners.CompleteDeprovision(id); }
    }, () => Results.Conflict(new { error = "Another lifecycle operation is running." }), ct)).RequireAuthorization("admin");

api.MapPost("/servers/{id}/delete-completely", async (string id, DeleteServerRequest request,
    ServerDeletionService deletion, CancellationToken ct) =>
{
    var result = await deletion.DeleteCompletelyAsync(id, request, ct);
    if (result.Success) return Results.Ok(result);
    return result.Code switch
    {
        "not_found" => Results.NotFound(result),
        "busy" => Results.Conflict(result),
        _ => Results.BadRequest(result)
    };
}).RequireAuthorization("admin");

api.MapGet("/servers/{id}/relay", async (string id, PublicRelayManager relay, CancellationToken ct) =>
{
    try { return Results.Ok(await relay.GetStatusAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapPut("/servers/{id}/relay", async (string id, PublicRelayServerSettings settings,
    ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    try
    {
        var running = tracker.Get(id).ProcessState == ProcessState.Running;
        return Results.Ok(await relay.SaveAsync(id, settings, running, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapDelete("/servers/{id}/relay", async (string id, ServerRegistry reg, PublicRelayManager relay, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();
});

api.MapGet("/servers/{id}/config", async (
    string id,
    HttpContext ctx,
    ServerRegistry reg,
    GameConfigFactory factory,
    IEnumerable<IDynamicSchemaExtension> dynamicExtensions,
    CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var schema = ConfigSchemas.For(def);
    var handler = factory.For(def);
    var values = handler is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                 : await handler.ReadAsync(def, ct);
    var sections = new List<ConfigSection>();
    if (schema is not null) sections.AddRange(schema.Sections);
    var curatedKeys = new HashSet<string>(
        schema?.AllFields.Select(f => f.Key) ?? Array.Empty<string>(),
        StringComparer.OrdinalIgnoreCase);
    foreach (var ext in dynamicExtensions)
    {
        if (!ext.Supports(def)) continue;
        var dyn = await ext.BuildAsync(def, curatedKeys, ct);
        if (dyn is null) continue;
        sections.Add(dyn.Section);
        foreach (var (k, v) in dyn.Values) values[k] = v;
        foreach (var f in dyn.Section.Fields) curatedKeys.Add(f.Key);
    }
    var composedSchema = schema is null
        ? (sections.Count == 0 ? null : new ConfigSchema("auto", def.Name, sections.ToArray()))
        : schema with { Sections = sections.ToArray() };
    // Vendor config keys may differ in casing from schema fields. The browser
    // compares JSON property names case-sensitively, including password fields.
    var displayValues = composedSchema is null ? values : ManagedConfigValues.Canonicalize(composedSchema, values);
    // Dragonwilds join password is visible only to Admin. Deprecated INI
    // admin credentials are excluded from DragonwildsConfig.ReadAsync itself.
    if (def.SteamAppId == "4019830" &&
        !ctx.User.HasClaim(TokenAuthHandler.RoleClaim, "Admin"))
        displayValues.Remove("WorldPassword");
    return Results.Ok(new { schema = composedSchema, values = displayValues });
});

api.MapPost("/servers/{id}/logs/tail", (string id, LogTailRequest req, ServerRegistry reg, LogTailService tail) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (req.Action.Equals("start", StringComparison.OrdinalIgnoreCase))
    {
        var ok = tail.Start(id);
        return ok ? Results.Ok(new { tailing = true }) : Results.BadRequest(new { error = "Could not start tail (no LogPathInGuest or not BareMetal?)" });
    }
    if (req.Action.Equals("stop", StringComparison.OrdinalIgnoreCase))
    {
        tail.Stop(id);
        return Results.Ok(new { tailing = false });
    }
    return Results.BadRequest(new { error = "action must be 'start' or 'stop'" });
});
api.MapGet("/servers/{id}/logs/tail", (string id, LogTailService tail) =>
    Results.Ok(new { tailing = tail.IsTailing(id) }));

api.MapGet("/servers/{id}/autostart", async (string id, ServerRegistry reg, IWindowsRunnerServiceManager runners, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (!runners.Supports(def))
        return Results.Ok(new { supported = false, enabled = (bool?)null });
    var enabled = await runners.GetAutostartAsync(def, ct);
    return Results.Ok(new { supported = true, enabled });
});
api.MapPost("/servers/{id}/autostart", async (string id, AutostartRequest req, ServerRegistry reg, IWindowsRunnerServiceManager runners, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (!runners.Supports(def))
        return Results.BadRequest(new { error = "Autostart is supported for Windows BareMetal Runner servers." });
    var (ok, error) = await runners.SetAutostartAsync(def, req.Enabled, ct);
    return ok ? Results.Ok(new { enabled = req.Enabled }) : Results.BadRequest(new { error });
});

static string ModUnsupportedReason(ServerDef def) => def.SteamAppId switch
{
    "2394010" => "Palworld doesn't have a centralized mod marketplace yet. Server-side admin tools like Palguard exist — install manually into PalServer\\Pal\\Binaries\\Win64\\. Follow Palguard's README on GitHub.",
    "4129620" => "Windrose's modding ecosystem is still developing — no community marketplace yet. Watch the official Windrose Discord for emerging tools.",
    "4019830" => "RuneScape: Dragonwilds does not have a supported centralized mod marketplace in GameServerControl yet. Manage server-side mods manually for now.",
    "2430930" => "ARK: Survival Ascended distributes mods through Steam Workshop, not Thunderstore. Workshop-based mod management isn't wired into the dashboard yet.",
    "376030"  => "ARK: Survival Evolved distributes mods through Steam Workshop, not Thunderstore. Workshop-based mod management isn't wired into the dashboard yet.",
    _         => "No mod manager registered for this game. Manage mods on disk for now."
};

api.MapGet("/servers/{id}/mods", async (string id, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.Ok(new ModListResponse(Array.Empty<ModInfo>(), false, ModUnsupportedReason(def), null));
    var list = await mgr.ListAsync(def, ct);
    return Results.Ok(new ModListResponse(list, true, null, mgr.ModsFolder(def)));
});
api.MapGet("/servers/{id}/mods/search", async (string id, string? q, int? limit, bool? serverSideOnly, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.Ok(new ModSearchResponse(Array.Empty<ModSearchResult>(), false, null, ModUnsupportedReason(def)));
    var results = await mgr.SearchAsync(def, q ?? "", limit ?? 30, serverSideOnly ?? true, ct);
    return Results.Ok(new ModSearchResponse(results, true, mgr.MarketplaceSource(def), null));
});
api.MapPost("/servers/{id}/mods/install", async (string id, ModInstallRequest req, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.BadRequest(new ModInstallResult(false, null, "Mod management not supported for this game."));
    var result = await mgr.InstallFromUrlAsync(def, req.Url, req.DisplayName, ct);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});
api.MapDelete("/servers/{id}/mods/{modId}", async (string id, string modId, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.BadRequest(new { error = "Mod management not supported for this game." });
    var ok = await mgr.UninstallAsync(def, modId, ct);
    return ok ? Results.Ok(new { uninstalled = modId }) : Results.NotFound(new { error = "Mod not found." });
});
api.MapGet("/servers/{id}/mods/updates", async (string id, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.Ok(new ModUpdatesResponse(Array.Empty<ModUpdateInfo>(), false, ModUnsupportedReason(def)));
    var updates = await mgr.CheckUpdatesAsync(def, ct);
    return Results.Ok(new ModUpdatesResponse(updates, true, null));
});
api.MapPost("/servers/{id}/mods/{modId}/update", async (string id, string modId, ModInstallRequest req, ServerRegistry reg, GameServerControl.Agent.Mods.ModManagerRegistry mods, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var mgr = mods.For(def);
    if (mgr is null) return Results.BadRequest(new ModInstallResult(false, null, "Mod management not supported for this game."));
    // Never uninstall a working mod before a replacement is downloaded.
    // Each manager now stages its payload and swaps files with rollback.
    var installed = await mgr.ListAsync(def, ct);
    if (!installed.Any(item => string.Equals(item.ModId, modId, StringComparison.OrdinalIgnoreCase)))
        return Results.NotFound(new ModInstallResult(false, null, "Installed mod not found."));
    // The manager validates the actual payload identity *before* replacing files
    // or writing metadata. Do not try to roll back a mismatched update afterward.
    var result = await mgr.InstallFromUrlAsync(def, req.Url, req.DisplayName, ct, expectedModId: modId);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

api.MapGet("/servers/{id}/rcon/players", async (string id, ServerRegistry reg, RconService rcon, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    return Results.Ok(await rcon.ListPlayersAsync(def, ct));
});
api.MapPost("/servers/{id}/rcon/command", async (string id, RconCommandRequest req, ServerRegistry reg, RconService rcon, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var result = await rcon.RunAsync(def, req.Command, req.Payload, ct);
    return Results.Ok(result);
});

api.MapPut("/servers/{id}/config", async (string id, Dictionary<string, string> values, ServerRegistry reg, GameConfigFactory factory, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    var handler = factory.For(def);
    if (handler is null) return Results.BadRequest(new { error = "No config handler for this game." });
    try
    {
        var ok = await handler.WriteAsync(def, values ?? new Dictionary<string, string>(), ct);
        return ok ? Results.Ok(new { written = true }) : Results.StatusCode(502);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/servers/{id}/status", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
{
    try { return Results.Ok(await orch.RefreshStatusAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapGet("/status", (StatusTracker tracker) => Results.Ok(tracker.Snapshot()));
api.MapPost("/servers/{id}/start", async (string id, ServerOrchestrator orch, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, ServerLifecycleGate lifecycleGate, CancellationToken ct) =>
{
    var result = await orch.StartAsync(id, ct);
    if (result.Success)
    {
        try
        {
            // Reconcile from current state, not the outcome of an older action.
            // If another lifecycle operation has begun, it owns the next sync.
            await lifecycleGate.TryRunIdleAsync(id, async () =>
            {
                var def = reg.Get(id);
                if (def is not null)
                    await relay.EnsureForServerAsync(def, tracker.Get(id).ProcessState == ProcessState.Running, ct);
            }, ct);
        }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/stop", async (string id, bool? stopVm, bool? force, ServerOrchestrator orch, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, ServerLifecycleGate lifecycleGate, CancellationToken ct) =>
{
    var result = await orch.StopAsync(id, stopVm ?? true, force ?? false, ct);
    if (result.Success)
    {
        try
        {
            // Reconcile from current state, not the outcome of an older action.
            // If another lifecycle operation has begun, it owns the next sync.
            await lifecycleGate.TryRunIdleAsync(id, async () =>
            {
                var def = reg.Get(id);
                if (def is not null)
                    await relay.EnsureForServerAsync(def, tracker.Get(id).ProcessState == ProcessState.Running, ct);
            }, ct);
        }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/restart", async (string id, ServerOrchestrator orch, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, ServerLifecycleGate lifecycleGate, CancellationToken ct) =>
{
    var result = await orch.RestartAsync(id, ct);
    if (result.Success)
    {
        try
        {
            // Reconcile from current state, not the outcome of an older action.
            // If another lifecycle operation has begun, it owns the next sync.
            await lifecycleGate.TryRunIdleAsync(id, async () =>
            {
                var def = reg.Get(id);
                if (def is not null)
                    await relay.EnsureForServerAsync(def, tracker.Get(id).ProcessState == ProcessState.Running, ct);
            }, ct);
        }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/backup", async (string id, BackupService backups,
    ServerDiagnostics diagnostics, ServerRegistry reg, DiscordNotifier discord, CancellationToken ct) =>
    Results.Ok(await diagnostics.TrackAsync(id, "Backup", () => backups.CreateAsync(id, ct), reg.Get(id), discord, ct)));
api.MapPost("/servers/{id}/update", async (string id, ServerOrchestrator orch,
    ServerDiagnostics diagnostics, ServerRegistry reg, DiscordNotifier discord, CancellationToken ct) =>
    Results.Ok(await diagnostics.TrackAsync(id, "Update", () => orch.UpdateAsync(id, ct), reg.Get(id), discord, ct)));
api.MapPost("/servers/{id}/apply", async (string id, ServerOrchestrator orch, CancellationToken ct) => Results.Ok(await orch.ApplyConfigAsync(id, ct)));

api.MapPost("/servers/{id}/backups", async (string id, BackupCreateRequest req, BackupService backups,
    ServerDiagnostics diagnostics, ServerRegistry reg, DiscordNotifier discord, CancellationToken ct) =>
{
    try { return Results.Ok(await diagnostics.TrackAsync(id, "Backup",
        () => backups.CreateAsync(id, req.Label, ct), reg.Get(id), discord, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("admin");
api.MapGet("/servers/{id}/backups/settings", (string id, BackupService backups) =>
{
    try { return Results.Ok(backups.GetSettings(id)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapPut("/servers/{id}/backups/settings", (string id, BackupSettings settings, BackupService backups) =>
{
    try { return Results.Ok(backups.SaveSettings(id, settings.Retention)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("admin");
api.MapGet("/servers/{id}/backups", async (string id, BackupService backups, CancellationToken ct) =>
{
    try { return Results.Ok(await backups.ListAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapPost("/servers/{id}/backups/{name}/restore", async (string id, string name, BackupService backups,
    ServerDiagnostics diagnostics, ServerRegistry reg, DiscordNotifier discord, CancellationToken ct) =>
    Results.Ok(await diagnostics.TrackAsync(id, "Restore",
        () => backups.RestoreAsync(id, name, ct), reg.Get(id), discord, ct))).RequireAuthorization("admin");
api.MapDelete("/servers/{id}/backups/{name}", async (string id, string name, BackupService backups, CancellationToken ct) =>
{
    try { return await backups.DeleteAsync(id, name, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("admin");

api.MapGet("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    return Results.Ok(await sched.ReadAsync(id, ct));
});
api.MapPost("/servers/{id}/schedule", async (string id, MaintenanceSchedule schedule, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    try
    {
        await sched.ApplyAsync(id, schedule, ct);
        return Results.Ok(new { applied = true });
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
    {
        return Results.BadRequest(new { applied = false, error = ex.Message });
    }
});
api.MapDelete("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    try
    {
        await sched.ApplyAsync(id, null, ct);
        return Results.NoContent();
    }
    catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/discord/test", async (DiscordWebhookTestRequest req, DiscordNotifier notifier, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.WebhookUrl)) return Results.BadRequest("WebhookUrl required");
    try
    {
        await notifier.SendRawAsync(req.WebhookUrl, "✅ Webhook test", "If you can see this in Discord, GameServerControl can post here.", ct);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
}).RequireAuthorization("admin");

api.MapGet("/tokens", (TokenRegistry tokens, HttpContext ctx) =>
{
    var role = ctx.User.FindFirst(TokenAuthHandler.RoleClaim)?.Value;
    return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
        ? Results.Ok(tokens.ListMetadata())
        : Results.Forbid();
});
api.MapPost("/tokens", (CreateTokenRequest req, TokenRegistry tokens) =>
{
    if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Id and Name required");
    try
    {
        var rec = tokens.Create(req.Id, req.Name, req.Role);
        return Results.Ok(rec);
    }
    catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
});
api.MapDelete("/tokens/{id}", (string id, TokenRegistry tokens) =>
    tokens.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();

// Deterministic integration fixtures may disable the background loop;
// production hosts always run it.
if (!(app.Environment.IsEnvironment("Testing") && app.Configuration.GetValue<bool>("Agent:DisableStatusMonitoring")))
_ = Task.Run(async () =>
{
    var stopping = app.Lifetime.ApplicationStopping;
    var orch = app.Services.GetRequiredService<ServerOrchestrator>();
    var reg = app.Services.GetRequiredService<ServerRegistry>();
    var relay = app.Services.GetRequiredService<PublicRelayManager>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    var diagnostics = app.Services.GetRequiredService<ServerDiagnostics>();
    var discord = app.Services.GetRequiredService<DiscordNotifier>();
    var alertPolicy = app.Services.GetRequiredService<ServerAlertPolicy>();
    var tracker = app.Services.GetRequiredService<StatusTracker>();
    var pollPolicy = new ServerStatusPollPolicy();
    var cadence = new ServerStatusCadence();
    var poller = app.Services.GetRequiredService<ServerStatusPoller>();
    var lifecycleGate = app.Services.GetRequiredService<ServerLifecycleGate>();

    // PeriodicTimer uses a fixed five-second tick instead of adding five
    // seconds *after* a slow cycle. Only one cycle is active at any time;
    // individual server reads may run concurrently, but Relay mutations stay
    // serialized and lifecycle Start/Stop/Update are never launched here.
    using var timer = new PeriodicTimer(ServerStatusCadence.CycleInterval);
    while (!stopping.IsCancellationRequested)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var servers = reg.All;
        var attempted = 0;
        var syncRelay = false;
        try
        {
            var now = DateTimeOffset.UtcNow;
            syncRelay = cadence.ShouldSyncRelay(now);
            var checkDisk = cadence.ShouldCheckDisk(now);

            // Unknown/running servers are checked each five-second tick.
            // Known stopped servers are checked no more often than 15 seconds.
            var ids = servers
                .Where(s => pollPolicy.ShouldPoll(s.Id, tracker.Get(s.Id).ProcessState, now))
                .Select(s => s.Id)
                .ToArray();
            attempted = ids.Length;

            // Bound process/Runner reads across DIFFERENT server instances.
            // Each in-flight read is awaited before another cycle may begin;
            // cancellation does not detach tasks that might later modify state.
            await poller.PollAsync(ids,
                (id, token) => orch.RefreshStatusForMonitoringAsync(id, token), stopping);

            bool? relayReachable = null;
            if (syncRelay && servers.Any(s => relay.SettingsFor(s.Id).Enabled))
            {
                try { relayReachable = (await relay.GetGlobalStatusAsync(stopping)).Reachable; }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                catch (Exception ex) { relayReachable = false; log.LogDebug(ex, "Public relay health check failed"); }
            }

            // Relay operations touch shared routing state and remain sequential.
            foreach (var s in servers)
            {
                try
                {
                    if (!syncRelay && !checkDisk) continue;
                    await lifecycleGate.TryRunIdleAsync(s.Id, async () =>
                    {
                        // Read the latest definition and tracker state only
                        // after entering the publication boundary. Never use
                        // a snapshot from before another Start/Stop/Restore.
                        var def = reg.Get(s.Id);
                        if (def is null) return;
                        var status = tracker.Get(s.Id);
                        if (syncRelay)
                        {
                            var relayActive = status.ProcessState == ProcessState.Running && relay.SettingsFor(s.Id).Enabled;
                            var synced = true;
                            try { await relay.EnsureForServerAsync(def, status.ProcessState == ProcessState.Running, stopping); }
                            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                            catch (Exception ex) { synced = false; log.LogDebug(ex, "Public relay sync failed for {Id}", s.Id); }
                            if (alertPolicy.ReportRelay(s.Id, relayActive, synced && (!relayActive || relayReachable == true)))
                            {
                                diagnostics.Record(s.Id, "RelayUnavailable", "Error", "Public VPS Relay could not be reached or synchronized.");
                                _ = discord.SendAsync(def, DiscordNotifier.Kind.Warn, "Public VPS Relay unavailable",
                                    "The Agent cannot reach or synchronize the public Relay. Check Server Network settings.");
                            }
                        }
                        if (checkDisk && status.ProcessState == ProcessState.Running &&
                            alertPolicy.ReportFreeDisk(s.Id, ServerAlertPolicy.AvailableDiskBytes(def.GuestWorkingDir)))
                        {
                            diagnostics.Record(s.Id, "LowDisk", "Error", "Available space on the game server volume is below 5 GiB.");
                            _ = discord.SendAsync(def, DiscordNotifier.Kind.Warn, "Low game server disk space",
                                "Less than 5 GiB is available on the game server volume. Check storage before the next save or backup.");
                        }
                    }, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
                catch (Exception ex) { log.LogDebug(ex, "Status monitoring failed for {Id}", s.Id); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
        catch (Exception ex) { log.LogError(ex, "Status cycle failed; monitoring will retry on the next tick."); }
        finally
        {
            poller.RecordCycle(System.Diagnostics.Stopwatch.GetElapsedTime(started),
                servers.Count, attempted, syncRelay);
        }

        try { if (!await timer.WaitForNextTickAsync(stopping)) break; }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
    }
});

app.Run();

public partial class Program { }

public record RconCommandRequest(RconStandardCommand Command, string? Payload);
public record AutostartRequest(bool Enabled);
public record LogTailRequest(string Action);
internal sealed record AutoUpdatePreferences(bool AutoUpdateOnStart, bool AutoUpdateOnRestart);
