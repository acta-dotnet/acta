namespace Acta.AspNetCore.Web;

/// <summary>
/// Extension-to-content-type mapping for embedded dashboard assets.
/// </summary>
internal static class ContentTypeMap
{
    public static string For(string path)
    {
        var dot = path.LastIndexOf('.');
        var extension = dot < 0 ? "" : path[dot..].ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".json" => "application/json; charset=utf-8",
            ".ico" => "image/x-icon",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
