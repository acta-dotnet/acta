namespace Acta.AspNetCore.Configuration;

/// <summary>
/// Host-facing options for <c>MapActa</c>: the dashboard UI knobs plus the inherited endpoint
/// options (controls, confirmation header, authorization hook).
/// </summary>
public sealed class ActaDashboardOptions : ActaEndpointOptions
{
    /// <summary>Whether the dashboard HTML and asset endpoints are mapped at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Serve a host page that loads the Vite dev server instead of embedded assets.</summary>
    public bool UseViteDevServer { get; set; }

    /// <summary>Vite dev server origin used when <see cref="UseViteDevServer"/> is set.</summary>
    public string ViteDevServerUrl { get; set; } = "http://localhost:5173";

    /// <summary>Whether dashboard responses carry the security headers, including CSP.</summary>
    public bool AddSecurityHeaders { get; set; } = true;
}
