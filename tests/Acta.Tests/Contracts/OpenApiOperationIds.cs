using Acta.AspNetCore;
using Acta.Tests.Conformance.Testing;

namespace Acta.Tests.Contracts;

/// <summary>
/// The operation id of every route, by its method and its path below Acta's version segment, so a route reads the same
/// under any mount: <see cref="OperationNames.OperationId"/> of the operation the operations index attributes the route
/// to, such as <c>pauseJob</c> or <c>listJobTags</c>. A generated client names its methods after these; a route the
/// index attributes to no operation fails generation. Endpoint names stay unset in the product: ASP.NET wants them
/// unique across the app, and a host may mount the API twice.
/// </summary>
internal static class OpenApiOperationIds
{
    private static readonly Lazy<List<(string Verb, string Pattern, string Operation)>> RouteMap = new(() =>
        OperationsIndexTests.RouteMap(IntegrationConfig.FindRepoRoot())
    );

    /// <summary>The id of <paramref name="method"/> on <paramref name="relativePath"/>, under any mount.</summary>
    public static string For(string method, string relativePath) => For(Key(method, relativePath), RouteMap.Value);

    /// <summary>The id of the route keyed <c>METHOD /path</c> among <paramref name="routes"/>; throws for one they lack.</summary>
    public static string For(string key, IReadOnlyCollection<(string Verb, string Pattern, string Operation)> routes) =>
        routes.FirstOrDefault(route => $"{route.Verb} {route.Pattern}" == key) is { Operation: not null } found
            ? OperationNames.OperationId(
                found.Operation,
                found.Pattern,
                [.. routes.Where(route => route.Operation == found.Operation).Select(route => route.Pattern)]
            )
            : throw new InvalidOperationException($"{key} serves no operation the operations index knows, so it has no operation id.");

    /// <summary>
    /// The route below Acta's <c>api/v1</c> root as <c>METHOD /path</c>, such as <c>POST /jobs/{jobRef}/purge</c> under any
    /// mount, one named like the version segment included.
    /// </summary>
    public static string Key(string method, string relativePath)
    {
        var segments = relativePath.Trim('/').Split('/');
        var version = ActaEndpointRouteBuilderExtensions.ApiVersionSegment.Trim('/');
        var root = Enumerable.Range(0, segments.Length - 1).LastOrDefault(i => segments[i] == "api" && segments[i + 1] == version, -1);
        return root < 0
            ? throw new InvalidOperationException($"'{relativePath}' has no 'api/{version}' root to key an operation id below.")
            : $"{method.ToUpperInvariant()} /{string.Join('/', segments.Skip(root + 2))}";
    }
}
