using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GameServerControl.Agent.Auth;

public sealed class TokenAuthOptions : AuthenticationSchemeOptions
{
    // Legacy field — kept for back-compat. The actual token list now lives in TokenRegistry,
    // which falls back to <c>Agent:ApiToken</c> automatically.
    public string Token { get; set; } = "";
}

public sealed class TokenAuthHandler : AuthenticationHandler<TokenAuthOptions>
{
    public const string SchemeName = "Token";
    public const string RoleClaim = "gsc:role";
    public const string TokenIdClaim = "gsc:tokenId";
    private const string ThrottleRetryAfterItem = "gsc:auth-throttle-retry-after";

    private readonly TokenRegistry _registry;
    private readonly AuthenticationFailureThrottle _failureThrottle = AuthenticationFailureThrottle.Shared;

    public TokenAuthHandler(IOptionsMonitor<TokenAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder, TokenRegistry registry)
        : base(options, logger, encoder)
    {
        _registry = registry;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var clientKey = ProtectedClientKey();
        string? presented = null;

        if (Request.Headers.TryGetValue("Authorization", out var auth))
        {
            var s = auth.ToString();
            if (s.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                presented = s.Substring("Bearer ".Length).Trim();
        }

        // SignalR's browser transport may need to send its bearer token through the
        // access_token query parameter. Do not accept query-string credentials on normal
        // API routes, where URLs can be retained by logs, history and intermediary tooling.
        var isLocalHub = Request.Path.StartsWithSegments("/hubs/status");
        var isRemoteHub = Request.Path.StartsWithSegments("/api/hosts") &&
            string.Equals(Request.Path.Value?.Split('/').LastOrDefault(), "status", StringComparison.Ordinal) &&
            Context.WebSockets.IsWebSocketRequest;
        if (presented is null && (isLocalHub || isRemoteHub) &&
            Request.Query.TryGetValue("access_token", out var q))
            presented = q.ToString();

        var now = DateTimeOffset.UtcNow;
        var retryAfter = TimeSpan.Zero;
        var throttled = clientKey is not null && _failureThrottle.TryGetRetryAfter(clientKey, now, out retryAfter);

        if (string.IsNullOrEmpty(presented))
        {
            if (throttled)
            {
                Context.Items[ThrottleRetryAfterItem] = retryAfter;
                return Task.FromResult(AuthenticateResult.Fail("authentication temporarily throttled"));
            }
            if (clientKey is not null) _failureThrottle.RecordFailure(clientKey, now);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The release installer creates a short-lived local token to prove the replacement
        // process can serve authenticated health traffic. It is intentionally invalid on
        // every other API route and disappears as soon as the update is accepted or rolled back.
        if (string.Equals(Request.Path.Value, "/api/health", StringComparison.Ordinal) &&
            UpdateHealthToken.IsValid(AppContext.BaseDirectory, presented))
        {
            if (clientKey is not null && !throttled) _failureThrottle.RecordSuccess(clientKey);
            Response.Headers["X-GameServerControl-Version"] = UpdateHealthToken.CurrentVersion;
            Response.Headers["X-GameServerControl-Pid"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            Response.Headers.CacheControl = "no-store";

            var healthIdentity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Update health probe"),
                new Claim(TokenIdClaim, "update-health-probe"),
                new Claim(RoleClaim, "ReadOnly")
            }, Scheme.Name);
            var healthPrincipal = new ClaimsPrincipal(healthIdentity);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(healthPrincipal, Scheme.Name)));
        }

        var match = _registry.Lookup(presented);
        if (match is null)
        {
            if (throttled)
            {
                Context.Items[ThrottleRetryAfterItem] = retryAfter;
                return Task.FromResult(AuthenticateResult.Fail("authentication temporarily throttled"));
            }
            if (clientKey is not null) _failureThrottle.RecordFailure(clientKey, now);
            return Task.FromResult(AuthenticateResult.Fail("invalid token"));
        }

        if (clientKey is not null && !throttled) _failureThrottle.RecordSuccess(clientKey);

        var (id, name, role) = match.Value;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(TokenIdClaim, id),
            new Claim(RoleClaim, role.ToString())
        }, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.TryGetValue(ThrottleRetryAfterItem, out var value) && value is TimeSpan retryAfter)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            Response.Headers.CacheControl = "no-store";
            await Response.WriteAsJsonAsync(new { error = "Too many authentication failures. Try again later." });
            return;
        }

        await base.HandleChallengeAsync(properties);
    }

    private string? ProtectedClientKey()
    {
        if (!Request.Path.StartsWithSegments("/api") && !Request.Path.StartsWithSegments("/hubs"))
            return null;
        return Context.Connection.RemoteIpAddress?.ToString();
    }
}
