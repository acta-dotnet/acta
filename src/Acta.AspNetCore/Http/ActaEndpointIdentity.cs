using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Acta.AspNetCore;

/// <summary>
/// Tags every operator endpoint by its resource, read from its own route, and refuses one that declares no
/// <see cref="ActaOperationAttribute"/>, the home of its operation id and permission. Without a tag, ASP.NET groups
/// the operations of <c>docs/reference/openapi.json</c> under the entry assembly's name. Endpoint names stay unset:
/// ASP.NET wants them unique across the app, and a host may map the API twice, a read-only mount beside a control one,
/// so the id is the attribute's, the same under any mount.
/// </summary>
internal static class ActaEndpointIdentity
{
    public static void Apply(RouteGroupBuilder group)
    {
        // Add and Finally are explicit IEndpointConventionBuilder members on RouteGroupBuilder.
        var conventions = (IEndpointConventionBuilder)group;
        conventions.Add(builder =>
        {
            if (builder is RouteEndpointBuilder route && Resource(route.RoutePattern) is { } resource)
            {
                route.Metadata.Add(new TagsAttribute(resource));
            }
        });
        // Finally runs after the route's own conventions, so it sees the attribute the route's WithMetadata added.
        conventions.Finally(builder =>
        {
            if (!builder.Metadata.OfType<ActaOperationAttribute>().Any())
            {
                throw new InvalidOperationException(
                    $"The Acta route {(builder as RouteEndpointBuilder)?.RoutePattern.RawText ?? builder.DisplayName} declares no ActaOperationAttribute."
                );
            }
        });
    }

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
