using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Tags every mapped operator endpoint by its resource, derived from the endpoint's own route and
/// applied once at the group rather than restated on 54 map calls.
/// </summary>
/// <remarks>
/// <para>
/// The tag is what groups operations in <c>docs/reference/openapi.json</c> and in any client
/// generated from it. Without one, ASP.NET falls back to the entry assembly's name, so the committed
/// contract was labelling every operation with whatever assembly happened to generate it.
/// </para>
/// <para>
/// Endpoint <em>names</em> are deliberately not set here. ASP.NET requires them to be globally unique
/// across the app, and Acta supports being mapped more than once - a read-only mount alongside a
/// control-enabled one is a supported deployment with its own test. A route-derived name would
/// collide between those mounts, and a mount-derived one would put the host's chosen path inside
/// every operation id in a document that is supposed to describe the API, not the deployment. So the
/// document carries no operation ids; the drift gate is over paths, methods, and shapes.
/// </para>
/// </remarks>
internal static class ActaEndpointIdentity
{
    public static void Apply(RouteGroupBuilder group) =>
        // Add is an explicit IEndpointConventionBuilder member on RouteGroupBuilder.
        ((IEndpointConventionBuilder)group).Add(builder =>
        {
            if (builder is RouteEndpointBuilder route && Resource(route.RoutePattern) is { } resource)
            {
                route.Metadata.Add(new TagsAttribute(resource));
            }
        });

    /// <summary>
    /// The first route segment below Acta's version segment - <c>jobs</c>, <c>schedules</c>,
    /// <c>tenants</c> - so the tag does not move when a host mounts the API somewhere other than
    /// <c>/acta/api</c>.
    /// </summary>
    private static string? Resource(RoutePattern pattern)
    {
        var version = ActaEndpointRouteBuilderExtensions.ApiVersionSegment.Trim('/');
        var below = false;

        foreach (var segment in pattern.PathSegments)
        {
            if (segment.Parts is not [RoutePatternLiteralPart literal])
            {
                continue;
            }

            if (below)
            {
                return char.ToUpperInvariant(literal.Content[0]) + literal.Content[1..];
            }

            below = literal.Content == version;
        }

        return null;
    }
}
