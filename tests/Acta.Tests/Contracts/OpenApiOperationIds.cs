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
        var segments = relativePath.Trim('/').Split('/');
        var version = ActaEndpointRouteBuilderExtensions.ApiVersionSegment.Trim('/');
        var below = Array.IndexOf(segments, version) + 1;
        if (below == 0)
        {
            throw new InvalidOperationException($"'{relativePath}' has no '{version}' segment to derive an operation id below.");
        }

        var id = new StringBuilder(Pascal(method.ToLowerInvariant()));
        foreach (var segment in segments.Skip(below))
        {
            id.Append(segment.StartsWith('{') ? "By" + Pascal(segment.Trim('{', '}')) : Pascal(segment));
        }
        return id.ToString();
    }

    private static string Pascal(string text) =>
        string.Concat(text.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}
