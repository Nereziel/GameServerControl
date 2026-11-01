using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GameServerControl.Relay;
using GameServerControl.Shared;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

if (!OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("GameServerControl Relay is a Linux-only component.");

var bind = builder.Configuration["Relay:Bind"] ?? "http://127.0.0.1:5100";
builder.WebHost.UseUrls(bind);

var apiToken = builder.Configuration["Relay:ApiToken"] ?? "";
if (apiToken.Length < 24)
    throw new InvalidOperationException("Relay:ApiToken must be configured with at least 24 characters.");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<RelayStateStore>();
builder.Services.AddSingleton<IptablesRelay>();
builder.Services.AddHostedService<RelayReconciler>();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var header = ctx.Request.Headers.Authorization.ToString();
    var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header[7..].Trim()
        : "";
    var expectedBytes = Encoding.UTF8.GetBytes(apiToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    if (expectedBytes.Length != suppliedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }
    await next();
});

var mutationGate = new SemaphoreSlim(1, 1);

app.MapGet("/api/health", (RelayStateStore store, IptablesRelay iptables) => Results.Ok(new
{
    ok = iptables.IpForwardingEnabled(),
    ipForwarding = iptables.IpForwardingEnabled(),
    publicInterface = iptables.PublicInterface,
    tailscaleInterface = iptables.TailscaleInterface,
    routeSets = store.Snapshot().Length,
    ts = DateTimeOffset.UtcNow
}));

app.MapGet("/api/routes", (RelayStateStore store) => Results.Ok(store.Snapshot()));
app.MapGet("/api/routes/{serverId}", (string serverId, RelayStateStore store) =>
{
    var route = store.Get(serverId);
    return route is null ? Results.NotFound() : Results.Ok(route);
});

app.MapPut("/api/routes/{serverId}", async (string serverId, RelayRouteDefinition definition,
    RelayStateStore store, IptablesRelay iptables, CancellationToken ct) =>
{
    if (!string.Equals(serverId, definition.ServerId, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Route serverId must match the URL." });
    if (!iptables.IpForwardingEnabled())
        return Results.Problem("IPv4 forwarding is disabled on the relay host.", statusCode: StatusCodes.Status503ServiceUnavailable);

    await mutationGate.WaitAsync(ct);
    try
    {
        var routes = store.Snapshot()
            .Where(r => !string.Equals(r.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            .Append(definition)
            .ToArray();
        try
        {
            await iptables.RebuildAsync(routes, ct);
            store.Replace(routes);
            return Results.Ok(definition);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
    }
    finally { mutationGate.Release(); }
});

app.MapDelete("/api/routes/{serverId}", async (string serverId, RelayStateStore store,
    IptablesRelay iptables, CancellationToken ct) =>
{
    await mutationGate.WaitAsync(ct);
    try
    {
        var existing = store.Snapshot();
        var routes = existing.Where(r => !string.Equals(r.ServerId, serverId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (routes.Length == existing.Length) return Results.NoContent();
        try
        {
            await iptables.RebuildAsync(routes, ct);
            store.Replace(routes);
            return Results.NoContent();
        }
        catch (Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
    }
    finally { mutationGate.Release(); }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<IptablesRelay>()
            .RebuildAsync(Array.Empty<RelayRouteDefinition>(), CancellationToken.None)
            .GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not flush public relay rules during shutdown");
    }
});

app.Run();
