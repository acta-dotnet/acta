using System.Text;
using Acta.AspNetCore;

namespace Acta.Tests.Contracts;

/// <summary>
/// The operation id of one route, derived rather than declared: the HTTP method, then each segment of the route
/// below Acta's version segment, a literal PascalCased by its hyphens and a placeholder as <c>By</c> plus its name,
/// so <c>POST .../v1/jobs/{jobRef}/pause</c> is <c>PostJobsByJobRefPause</c> under any mount. Endpoint names stay
/// unset in the product: ASP.NET wants them unique across the app, and a host may mount the API twice.
/// </summary>
internal static class OpenApiOperationIds
{
    public static string For(string method, string relativePath)
    {
        var id = new StringBuilder(Pascal(method.ToLowerInvariant()));
        foreach (var segment in Below(relativePath))
        {
            id.Append(segment.StartsWith('{') ? "By" + Pascal(segment.Trim('{', '}')) : Pascal(segment));
        }
        return id.ToString();
    }

    /// <summary>The route below Acta's version segment as <c>METHOD /path</c>, such as <c>POST /jobs/{jobRef}/purge</c> under any mount.</summary>
    public static string Key(string method, string relativePath) => $"{method.ToUpperInvariant()} /{string.Join('/', Below(relativePath))}";

    private static IEnumerable<string> Below(string relativePath)
    {
        var segments = relativePath.Trim('/').Split('/');
        var version = ActaEndpointRouteBuilderExtensions.ApiVersionSegment.Trim('/');
        var below = Array.IndexOf(segments, version) + 1;
        return below == 0
            ? throw new InvalidOperationException($"'{relativePath}' has no '{version}' segment to derive an operation id below.")
            : segments.Skip(below);
    }

    private static string Pascal(string text) =>
        string.Concat(text.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}
