namespace Acta.AspNetCore;

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
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            _ => "application/octet-stream",
        };
    }
}
