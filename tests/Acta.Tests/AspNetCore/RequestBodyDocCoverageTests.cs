using Acta.AspNetCore.Web;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The body-side twin of <see cref="QueryDocCoverageTests"/>. A handler reads its JSON body through
/// ControlEndpointValidation rather than a bound parameter, so the OpenAPI document learns the body
/// only from the endpoint's <c>RequestBodyDoc</c> declaration: a write endpoint that forgets it is
/// simply absent from the contract, with nothing failing. This holds every mapped POST and PATCH to
/// carrying a declaration, with the genuinely bodyless writes pinned as a literal allowlist.
/// </summary>
public sealed class RequestBodyDocCoverageTests
{
    /// <summary>
    /// Mapped POST/PATCH operations that read no request body at all: a route whose whole input sits
    /// in the path, leaving nothing for a body schema to describe. Empty today - all 33 write
    /// operations read a body, even the ones whose body is optional. An entry here is a deliberate
    /// claim that the handler never reads the body, and it is checked in both directions.
    /// </summary>
    private static readonly HashSet<string> NoBodyRoutes = new(StringComparer.Ordinal) { };

    [Fact]
    public async Task Every_write_endpoint_declares_the_json_body_it_reads()
    {
        var (app, client) = await TestDashboardHost.StartAsync(o =>
        {
            // Controls on, dashboard UI off: the write surface is the whole point, and the SPA
            // fallback routes are not API operations.
            o.Enabled = false;
            o.EnableControls = true;
        });
        client.Dispose();
        await using var _ = app;

        var failures = new List<string>();
        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var declared = endpoint.Metadata.GetMetadata<RequestBodyDoc>() is not null;
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (
                var operation in methods
                    .Where(m => m is "POST" or "PATCH")
                    .Select(m => $"{m} /{endpoint.RoutePattern.RawText?.TrimStart('/')}")
            )
            {
                operations.Add(operation);
                if (NoBodyRoutes.Contains(operation))
                {
                    if (declared)
                    {
                        failures.Add($"{operation}: allowlisted as bodyless but declares a RequestBodyDoc; drop the allowlist entry.");
                    }
                    continue;
                }
                if (!declared)
                {
                    failures.Add(
                        $"{operation}: reads a request body without declaring it. Add .AcceptsJson<TBody>() "
                            + "to the mapping, or allowlist the route here if the handler truly reads no body."
                    );
                }
            }
        }

        foreach (var stale in NoBodyRoutes.Except(operations).Order(StringComparer.Ordinal))
        {
            failures.Add($"{stale}: allowlisted but no such operation is mapped; the route was renamed or removed.");
        }

        // Floor against a scan that silently stops seeing the graph and passes on an empty set. The
        // write surface is 33 operations today; the floor sits just under it so a route removal is a
        // normal diff while a broken scan is a failure.
        Assert.True(operations.Count >= 30, $"Only {operations.Count} write operations found; the endpoint scan is broken.");
        Assert.True(failures.Count == 0, "Request-body declarations and the write surface diverged:\n" + string.Join('\n', failures));
    }
}
