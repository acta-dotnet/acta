using System.Text.Json;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore;
using Acta.Tests.Conformance.Testing;
using Acta.Tests.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds the HTTP surface to "a route's bodies are named for its operation" in <c>CONTRIBUTING.md</c> (Naming conventions),
/// read from what the mapped endpoints declare: a route's JSON body is its own <c>XRequest</c>, and no body serves two
/// routes unless both serve the operation it is named for, whose request they share; what it answers is an
/// <c>XResponse</c>, the SDK's or one declared in <c>src/Acta.AspNetCore/Operations/Group/X/</c>,
/// or the payload itself as the body, typeless in JSON or raw bytes, when the route only renders the payload its
/// operation answers; and every request and response record in Acta.AspNetCore sits in the folder named for it.
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
        // Routes that serve one operation share its request; any other body serves one route.
        var operationOf = OperationsIndexTests
            .RouteMap(IntegrationConfig.FindRepoRoot())
            .DistinctBy(route => $"{route.Verb} {route.Pattern}")
            .ToDictionary(route => $"{route.Verb} {route.Pattern}", route => route.Operation, StringComparer.Ordinal);
        foreach (
            var shared in bodies
                .GroupBy(route => route.Body!)
                .Where(group => group.Count() > 1)
                .Where(group => group.Any(route => operationOf.GetValueOrDefault(route.Name) + "Request" != group.Key.Name))
        )
        {
            violations.Add(($"{shared.Key.Name} (serves {shared.Count()} routes)", string.Join(", ", shared.Select(route => route.Name))));
        }

        // The problem document is the API-wide error model, not an operation's answer.
        foreach (
            var (route, response) in routes
                .SelectMany(route => route.Responses.Select(response => (route.Name, Response: response)))
                .Where(entry => !typeof(ProblemDetails).IsAssignableFrom(entry.Response))
                .Where(entry => !IsResponse(entry.Response))
        )
        {
            violations.Add(($"{route} (answers {ShippedCode.SimpleName(response)})", "a route answers an XResponse"));
        }

        // A body declared with no type is the payload itself, in the payload's two content types and nothing else.
        foreach (var route in routes.Where(route => route.Typeless.Any(types => !types.SequenceEqual(PayloadContentTypes))))
        {
            violations.Add(($"{route.Name} (answers a typeless body)", "only a payload read answers the payload itself"));
        }

        // Every request and response record, whether a route names it or not, sits in the folder named for it.
        foreach (var role in (string[])["Request", "Response"])
        {
            foreach (var (name, path) in declared.Where(type => type.Name.EndsWith(role, StringComparison.Ordinal)))
            {
                if (!InOwnFolder(name, role, path))
                {
                    violations.Add(($"{name} (outside its route folder)", $"declared in {path}"));
                }
            }
        }

        Assert.True(bodies.Count > 27, $"Only {bodies.Count} routes declare a body; the endpoint scan is broken.");
        Assert.True(
            routes.Count(route => route.Typeless.Count > 0) >= 2,
            "No payload read declares its body; the endpoint scan is broken."
        );
        ArchitectureAssert.NoViolations(
            violations,
            "Give the route its own XRequest in src/Acta.AspNetCore/Operations/<Group>/X/, or the request of the operation it shares "
                + "with other routes, "
                + "and answer with the SDK's XResponse, the route's own when it composes, or the payload itself when it only renders one."
        );
    }

    private static readonly string[] PayloadContentTypes = [RawPayload.JsonContentType, RawPayload.BinaryContentType];

    // The SDK's response or the route's own, by name; where the route's own is declared is the folder rule's to check.
    private static bool IsResponse(Type type) =>
        !type.IsGenericType
        && type.Name.EndsWith("Response", StringComparison.Ordinal)
        && type.Assembly.GetName().Name is "Acta" or "Acta.AspNetCore";

    // Operations/<Group>/<Operation>/ below the route root, the operation being the type's name without its role.
    private static bool InOwnFolder(string name, string role, string path) =>
        path.StartsWith(RouteRoot, StringComparison.Ordinal)
        && path[RouteRoot.Length..].Split('/') is [_, var operation, _]
        && operation + role == name;

    /// <summary>
    /// Every route the API maps with controls on, by method and pattern below the version segment, with the content types
    /// of each body it declares without a type.
    /// </summary>
    private static async Task<
        List<(string Name, Type? Body, IReadOnlyList<Type> Responses, IReadOnlyList<IReadOnlyList<string>> Typeless)>
    > RoutesAsync()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options =>
        {
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
                            Body: endpoint.Metadata.GetMetadata<RequestBodyDoc>()?.BodyType,
                            Responses: (IReadOnlyList<Type>)
                                [
                                    // A status declared without a body, the host's 401, answers no type.
                                    .. endpoint
                                        .Metadata.OfType<IProducesResponseTypeMetadata>()
                                        .Select(produces => produces.Type)
                                        .OfType<Type>()
                                        .Where(type => type != typeof(void))
                                        .Distinct(),
                                ],
                            Typeless: (IReadOnlyList<IReadOnlyList<string>>)
                                [
                                    .. endpoint
                                        .Metadata.OfType<IProducesResponseTypeMetadata>()
                                        .Where(produces =>
                                            (produces.Type is null || produces.Type == typeof(void)) && produces.ContentTypes.Any()
                                        )
                                        .Select(produces => (IReadOnlyList<string>)[.. produces.ContentTypes]),
                                ]
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
