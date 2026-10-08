using System.Text.Json;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds the HTTP surface to "a route's bodies are named for its operation" in <c>docs/internals/naming-conventions.md</c>,
/// read from what the mapped endpoints declare: a route's JSON body is its own <c>XRequest</c>, declared in
/// <c>src/Acta.AspNetCore/Operations/Group/X/</c>, and no body serves two routes. What predates the rule is listed in the
/// <c>route-bodies</c> ratchet.
/// </summary>
public sealed class RouteBodyTests
{
    private const string RouteRoot = "src/Acta.AspNetCore/Operations/";

    private const string ApiPrefix = "/acta/api/v1";

    [Fact]
    public async Task Route_bodies_are_named_for_their_operation()
    {
        var routes = await RoutesAsync();
        var declared = Declarations();

        // A body Acta passes through verbatim, the signal value, is any JSON and has no record to name.
        var bodies = routes.Where(route => route.Body is { } body && body != typeof(JsonElement)).ToList();
        var violations = new List<(string Key, string? Detail)>();
        foreach (var route in bodies.Where(route => !route.Body!.Name.EndsWith("Request", StringComparison.Ordinal)))
        {
            violations.Add(($"{route.Name} (takes {route.Body!.Name})", "a route's body is its own XRequest"));
        }
        foreach (var shared in bodies.GroupBy(route => route.Body!).Where(group => group.Count() > 1))
        {
            violations.Add(($"{shared.Key.Name} (serves {shared.Count()} routes)", string.Join(", ", shared.Select(route => route.Name))));
        }

        // Every request record, whether a route binds it or not, sits in the folder named for it.
        foreach (var (name, path) in declared.Where(type => type.Name.EndsWith("Request", StringComparison.Ordinal)))
        {
            if (!InOwnFolder(name, "Request", path))
            {
                violations.Add(($"{name} (outside its route folder)", $"declared in {path}"));
            }
        }

        Assert.True(bodies.Count > 30, $"Only {bodies.Count} routes declare a body; the endpoint scan is broken.");
        Ratchet.Check(
            "route-bodies",
            violations,
            "Give the route its own XRequest in src/Acta.AspNetCore/Operations/<Group>/X/, sharing an interface rather than a record."
        );
    }

    // Operations/<Group>/<Operation>/ below the route root, the operation being the type's name without its role.
    private static bool InOwnFolder(string name, string role, string path) =>
        path.StartsWith(RouteRoot, StringComparison.Ordinal)
        && path[RouteRoot.Length..].Split('/') is [_, var operation, _]
        && operation + role == name;

    /// <summary>Every route the API maps with controls on, by method and pattern below the version segment.</summary>
    private static async Task<List<(string Name, Type? Body)>> RoutesAsync()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options =>
        {
            options.Enabled = false;
            options.EnableControls = true;
        });
        client.Dispose();
        await using var _ = app;

        return
        [
            .. app
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(ApiPrefix, StringComparison.Ordinal) == true)
                .SelectMany(endpoint =>
                    (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
                        (
                            Name: $"{method} {endpoint.RoutePattern.RawText![ApiPrefix.Length..]}",
                            Body: endpoint.Metadata.GetMetadata<RequestBodyDoc>()?.BodyType
                        )
                    )
                ),
        ];
    }

    /// <summary>The classes, records, and structs Acta.AspNetCore declares, with the file that declares each.</summary>
    private static List<(string Name, string Path)> Declarations() =>
        [
            .. ShippedCode
                .SourceFiles("*.cs")
                .Where(file => file.Relative.StartsWith("src/Acta.AspNetCore/", StringComparison.Ordinal))
                .SelectMany(file =>
                    CSharpSyntaxTree
                        .ParseText(File.ReadAllText(file.Path))
                        .GetRoot()
                        .DescendantNodes()
                        .OfType<TypeDeclarationSyntax>()
                        .Where(type => type is not InterfaceDeclarationSyntax)
                        .Select(type => (type.Identifier.Text, file.Relative))
                ),
        ];
}
