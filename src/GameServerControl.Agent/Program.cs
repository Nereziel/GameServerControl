using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Config;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Logs;
using GameServerControl.Agent.Networking;
using GameServerControl.Agent.Notifications;
using GameServerControl.Agent.Rcon;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

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
builder.Services.AddSingleton<LocalProcessService>();
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<PublicRelayManager>();
builder.Services.AddSingleton<StatusTracker>();
builder.Services.AddSingleton<ServerOrchestrator>();
builder.Services.AddSingleton<MaintenanceScheduler>();
builder.Services.AddSingleton<SteamCmdManager>();
builder.Services.AddSingleton<ServerInstaller>();
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
builder.Services.AddSingleton<GameServerControl.Agent.Mods.ThunderstoreClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.FicsitClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.GitHubReleasesClient>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.ValheimBepInExModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.SatisfactoryThunderstoreModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.PalworldCuratedModManager>();
builder.Services.AddSingleton<GameServerControl.Agent.Mods.IModManager, GameServerControl.Agent.Mods.ArkWorkshopModManager>();
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

app.MapGet("/", () =>
{
    var htmlPath = Path.Combine(AppContext.BaseDirectory, "WebUi", "index.html");
    return File.Exists(htmlPath)
        ? Results.File(htmlPath, "text/html; charset=utf-8")
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

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RoleEnforcementMiddleware>();
app.UseMiddleware<AuditMiddleware>();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/health", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }));
api.MapGet("/version", (UpdateChecker checker) => Results.Ok(checker.Latest));
api.MapPost("/version/check", async (UpdateChecker checker, CancellationToken ct) =>
    Results.Ok(await checker.CheckAsync(ct)));
api.MapGet("/game-profiles", () => Results.Ok(GameProfiles.All));
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
    var jobId = installer.StartJob(req);
    return Results.Ok(new InstallJobAck(jobId, "Install job queued"));
});
api.MapGet("/servers/install/{jobId}", (string jobId, ServerInstaller installer) =>
{
    var p = installer.Get(jobId);
    return p is null ? Results.NotFound() : Results.Ok(p);
});

api.MapPut("/servers/{id}", (string id, ServerDef def, ServerStore store) =>
{
    try { return Results.Ok(store.Update(id, def)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapDelete("/servers/{id}", async (string id, ServerStore store, PublicRelayManager relay, CancellationToken ct) =>
{
    if (!store.Delete(id)) return Results.NotFound();
    await relay.RemoveServerAsync(id, ct);
    return Results.NoContent();
});

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
    return Results.Ok(new { schema = composedSchema, values });
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

api.MapGet("/servers/{id}/autostart", async (string id, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.Ok(new { supported = false, enabled = (bool?)null });
    var enabled = await local.GetAutostartAsync(def.ScheduledTaskName, ct);
    return Results.Ok(new { supported = true, enabled });
});
api.MapPost("/servers/{id}/autostart", async (string id, AutostartRequest req, ServerRegistry reg, LocalProcessService local, CancellationToken ct) =>
{
    var def = reg.Get(id);
    if (def is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        return Results.BadRequest(new { error = "Server has no ScheduledTaskName set." });
    var ok = await local.SetAutostartAsync(def.ScheduledTaskName, req.Enabled, ct);
    return ok ? Results.Ok(new { enabled = req.Enabled }) : Results.StatusCode(502);
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
    await mgr.UninstallAsync(def, modId, ct);
    var result = await mgr.InstallFromUrlAsync(def, req.Url, req.DisplayName, ct);
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
api.MapPost("/servers/{id}/start", async (string id, ServerOrchestrator orch, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, CancellationToken ct) =>
{
    var result = await orch.StartAsync(id, ct);
    var def = reg.Get(id);
    if (result.Success && def is not null)
    {
        try { await relay.EnsureForServerAsync(def, tracker.Get(id).ProcessState == ProcessState.Running, ct); }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/stop", async (string id, bool? stopVm, bool? force, ServerOrchestrator orch, ServerRegistry reg, PublicRelayManager relay, CancellationToken ct) =>
{
    var result = await orch.StopAsync(id, stopVm ?? true, force ?? false, ct);
    var def = reg.Get(id);
    if (def is not null)
    {
        try { await relay.EnsureForServerAsync(def, false, ct); }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/restart", async (string id, ServerOrchestrator orch, ServerRegistry reg, StatusTracker tracker, PublicRelayManager relay, CancellationToken ct) =>
{
    var result = await orch.RestartAsync(id, ct);
    var def = reg.Get(id);
    if (result.Success && def is not null)
    {
        try { await relay.EnsureForServerAsync(def, tracker.Get(id).ProcessState == ProcessState.Running, ct); }
        catch (Exception ex) { result = result with { Message = result.Message + " Public relay warning: " + ex.Message }; }
    }
    return Results.Ok(result);
});
api.MapPost("/servers/{id}/backup", async (string id, ServerOrchestrator orch, CancellationToken ct) => Results.Ok(await orch.BackupAsync(id, ct)));
api.MapPost("/servers/{id}/update", async (string id, ServerOrchestrator orch, CancellationToken ct) => Results.Ok(await orch.UpdateAsync(id, ct)));
api.MapPost("/servers/{id}/apply", async (string id, ServerOrchestrator orch, CancellationToken ct) => Results.Ok(await orch.ApplyConfigAsync(id, ct)));

api.MapGet("/servers/{id}/backups", async (string id, ServerOrchestrator orch, CancellationToken ct) =>
{
    try { return Results.Ok(await orch.ListBackupsAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapPost("/servers/{id}/backups/{name}/restore", async (string id, string name, ServerOrchestrator orch, CancellationToken ct) => Results.Ok(await orch.RestoreBackupAsync(id, name, ct)));
api.MapDelete("/servers/{id}/backups/{name}", async (string id, string name, ServerOrchestrator orch, CancellationToken ct) => (await orch.DeleteBackupAsync(id, name, ct)) ? Results.NoContent() : Results.NotFound());

api.MapGet("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    return Results.Ok(await sched.ReadAsync(id, ct));
});
api.MapPost("/servers/{id}/schedule", async (string id, MaintenanceSchedule schedule, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    await sched.ApplyAsync(id, schedule, ct);
    return Results.Ok(new { applied = true });
});
api.MapDelete("/servers/{id}/schedule", async (string id, ServerRegistry reg, MaintenanceScheduler sched, CancellationToken ct) =>
{
    if (reg.Get(id) is null) return Results.NotFound();
    await sched.ApplyAsync(id, null, ct);
    return Results.NoContent();
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
});

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

_ = Task.Run(async () =>
{
    var orch = app.Services.GetRequiredService<ServerOrchestrator>();
    var reg = app.Services.GetRequiredService<ServerRegistry>();
    var relay = app.Services.GetRequiredService<PublicRelayManager>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        foreach (var s in reg.All)
        {
            try
            {
                var status = await orch.RefreshStatusAsync(s.Id, app.Lifetime.ApplicationStopping);
                try { await relay.EnsureForServerAsync(s, status.ProcessState == ProcessState.Running, app.Lifetime.ApplicationStopping); }
                catch (Exception ex) { log.LogDebug(ex, "Public relay sync failed for {Id}", s.Id); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogDebug(ex, "Status refresh failed for {Id}", s.Id); }
        }
        try { await Task.Delay(TimeSpan.FromSeconds(30), app.Lifetime.ApplicationStopping); }
        catch (OperationCanceledException) { break; }
    }
});

app.Run();

public record RconCommandRequest(RconStandardCommand Command, string? Payload);
public record AutostartRequest(bool Enabled);
public record LogTailRequest(string Action);
