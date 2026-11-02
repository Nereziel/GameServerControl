namespace GameServerControl.Agent.Security;

public static class WebSecurityHeadersMiddleware
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'none'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "script-src 'self'; " +
        "script-src-attr 'none'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' http: https: ws: wss:";

    public static void Apply(IHeaderDictionary headers)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }
}
