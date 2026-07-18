using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Security headers for dashboard HTML and asset responses. The development variant relaxes CSP
/// for the Vite dev server's inline module loader and websocket.
/// </summary>
internal static class DashboardSecurityHeaders
{
    private const string ProductionCsp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    public static void Apply(HttpResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] = ProductionCsp;
    }

    public static void ApplyDevelopment(HttpResponse response, string viteOrigin)
    {
        var ws = viteOrigin.Replace("http://", "ws://", StringComparison.Ordinal).Replace("https://", "wss://", StringComparison.Ordinal);
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] =
            $"default-src 'self'; script-src 'self' 'unsafe-inline' {viteOrigin}; style-src 'self' 'unsafe-inline' {viteOrigin}; "
            + $"img-src 'self' data:; connect-src 'self' {viteOrigin} {ws}; object-src 'none'; base-uri 'self'";
    }
}
