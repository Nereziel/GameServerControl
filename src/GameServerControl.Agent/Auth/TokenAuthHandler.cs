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
        if (clientKey is not null &&
            _failureThrottle.TryGetRetryAfter(clientKey, DateTimeOffset.UtcNow, out var retryAfter))
        {
            Context.Items[ThrottleRetryAfterItem] = retryAfter;
            return Task.FromResult(AuthenticateResult.Fail("authentication temporarily throttled"));
        }

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
        if (presented is null &&
            Request.Path.StartsWithSegments("/hubs/status") &&
            Request.Query.TryGetValue("access_token", out var q))
            presented = q.ToString();

        if (string.IsNullOrEmpty(presented))
        {
            if (clientKey is not null) _failureThrottle.RecordFailure(clientKey, DateTimeOffset.UtcNow);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Installers use a short-lived local credential to prove that the replacement
        // Agent is actually serving authenticated API traffic. This credential is valid
        // only for the exact health endpoint and cannot be used for any other API route.
        if (string.Equals(Request.Path.Value, "/api/health", StringComparison.Ordinal) &&
            UpdateHealthToken.IsValid(AppContext.BaseDirectory, presented))
        {
            if (clientKey is not null) _failureThrottle.RecordSuccess(clientKey);
            return Task.FromResult(Success(
                id: "update-health-probe",
                name: "Update health probe",
                role: TokenRole.ReadOnly));
        }

        var match = _registry.Lookup(presented);
        if (match is null)
        {
            if (clientKey is not null) _failureThrottle.RecordFailure(clientKey, DateTimeOffset.UtcNow);
            return Task.FromResult(AuthenticateResult.Fail("invalid token"));
        }

        if (clientKey is not null) _failureThrottle.RecordSuccess(clientKey);

        var (id, name, role) = match.Value;
        return Task.FromResult(Success(id, name, role));
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

    private AuthenticateResult Success(string id, string name, TokenRole role)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(TokenIdClaim, id),
            new Claim(RoleClaim, role.ToString())
        }, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private string? ProtectedClientKey()
    {
        if (!Request.Path.StartsWithSegments("/api") && !Request.Path.StartsWithSegments("/hubs"))
            return null;
        return Context.Connection.RemoteIpAddress?.ToString();
    }
}
