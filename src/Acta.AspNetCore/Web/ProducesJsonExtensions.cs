using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Declares a JSON success shape across a whole route group.
/// </summary>
/// <remarks>
/// The framework's <c>Produces&lt;T&gt;</c> only accepts a <c>RouteHandlerBuilder</c>, so a family of
/// endpoints that all answer with the same type would have to restate it once per endpoint.
/// <c>ProducesProblem</c> already has a group-wide overload; this is the same thing for the success
/// case, writing the metadata the OpenAPI document reads.
/// </remarks>
internal static class ProducesJsonExtensions
{
    // Takes the interface rather than a generic self-type: C# cannot infer one type argument and
    // require the other, so a generic builder parameter would force every call site to spell out
    // RouteGroupBuilder as well. These are statements, not fluent chains, so nothing is lost.
    public static void ProducesJson<TResponse>(this IEndpointConventionBuilder builder, int statusCode = StatusCodes.Status200OK) =>
        builder.Add(endpoint => endpoint.Metadata.Add(new ActaProducesResponse(statusCode, typeof(TResponse))));

    private sealed class ActaProducesResponse(int statusCode, Type type) : IProducesResponseTypeMetadata
    {
        public int StatusCode { get; } = statusCode;
        public Type? Type { get; } = type;
        public IEnumerable<string> ContentTypes { get; } = ["application/json"];
    }
}
