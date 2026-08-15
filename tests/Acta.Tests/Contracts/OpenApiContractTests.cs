using System.Text.Json.Nodes;
using Acta.AspNetCore.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// Freezes the HTTP contract the way <c>PersistedCodeContractTests</c> freezes the code vocabulary:
/// generate the document from the real endpoint graph, compare it to the committed file, fail on any
/// drift. Regenerate deliberately with
/// <c>ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests</c>, then read the
/// diff - an unintended route, status code, or payload change shows up there before it ships.
/// </summary>
/// <remarks>
/// The document is produced by a test rather than at build time so the shipped
/// <c>Acta.AspNetCore</c> package carries no OpenAPI dependency. Controls are mapped on
/// (<c>EnableControls</c>) because the frozen surface is the whole surface, not the read-only subset a
/// default host happens to expose.
/// </remarks>
public sealed class OpenApiContractTests
{
    private const string DocumentFile = "docs/reference/openapi.json";

    [Fact]
    public async Task Http_contract_matches_the_committed_document()
    {
        var generated = await GenerateAsync();
        var path = Path.Combine(RepoRoot(), DocumentFile.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable("ACTA_EMIT_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, Normalize(generated) + "\n", TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"{DocumentFile} missing: run with ACTA_EMIT_OPENAPI=1.");
        var committed = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            Normalize(committed) == Normalize(generated),
            $"{DocumentFile} is stale. The HTTP surface changed at "
                + FirstDifference(Normalize(committed), Normalize(generated))
                + " Regenerate: ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests"
        );
    }

    // Every operation carries a one-line summary, so a reader scanning the document (or Scalar's
    // operation list) never meets an unexplained action. New endpoints fail here until they say
    // what they are about via WithSummary.
    [Fact]
    public async Task Every_operation_carries_a_summary()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;
        var missing = new List<string>();
        foreach (var (path, operations) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in operations!.AsObject())
            {
                if (string.IsNullOrWhiteSpace(operation?["summary"]?.GetValue<string>()))
                {
                    missing.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        Assert.Empty(missing);
    }

    private static async Task<string> GenerateAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fake = new AspNetCore.TestDashboardHost.FakeJobs();
        builder.Services.AddSingleton<IJobs>(fake);
        builder.Services.AddSingleton<IActaOperations>(fake);
        builder.Services.AddOpenApi(o =>
        // Two request records carry `JsonElement Input = default` for the free-form payload. The
        // generator copies that default into the schema and then cannot serialize it, because an
        // uninitialized JsonElement has no value to write. The defaults document nothing anyway -
        // the field is "whatever JSON the caller sends" - so they are dropped rather than paid for
        // by weakening the request types to keep a generator happy.
        {
            // Without this the generator defaults info.title to "{entry assembly} | {document}",
            // which stamped this test host's name into the committed contract ("Acta.Tests | v1")
            // and onto every Scalar page serving it. The contract names the product, not whichever
            // host generated or serves it.
            o.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Acta API";
                    return Task.CompletedTask;
                }
            );
            o.AddSchemaTransformer(
                (schema, _, _) =>
                {
                    schema.Default = null;
                    return Task.CompletedTask;
                }
            );
            // The handlers bind query strings through QueryBinding rather than typed parameters,
            // so the generator cannot see the filter surface; each endpoint declares it as
            // QueryParameterDoc metadata (product-side, OpenAPI-free) and this transformer
            // renders it, which is what makes the committed document protect the filters.
            o.AddOperationTransformer(
                (operation, context, _) =>
                {
                    foreach (var doc in context.Description.ActionDescriptor.EndpointMetadata.OfType<QueryParameterDoc>())
                    {
                        var schema = doc.Kind switch
                        {
                            QueryParameterKind.Int => new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                            QueryParameterKind.Bool => new OpenApiSchema { Type = JsonSchemaType.Boolean },
                            QueryParameterKind.Instant => new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
                            _ => new OpenApiSchema { Type = JsonSchemaType.String },
                        };
                        if (doc.CodeKind is { } kind)
                        {
                            // The accepted values are the family's kebab codes, resolved from the
                            // model itself so the documented vocabulary cannot drift from it - a
                            // renamed member shows up as an openapi.json diff the gate makes a
                            // human read.
                            schema.Enum =
                            [
                                .. CodeManifests
                                    .All.Where(e => string.Equals(e.CodeKind, kind, StringComparison.Ordinal))
                                    .OrderBy(e => e.Id)
                                    .Select(e => (JsonNode)e.Code),
                            ];
                        }
                        operation.Parameters ??= [];
                        operation.Parameters.Add(
                            new OpenApiParameter
                            {
                                Name = doc.Name,
                                In = ParameterLocation.Query,
                                Required = doc.Required,
                                Description = doc.Description,
                                Schema = doc.Repeatable ? new OpenApiSchema { Type = JsonSchemaType.Array, Items = schema } : schema,
                            }
                        );
                    }
                    return Task.CompletedTask;
                }
            );
        });

        var app = builder.Build();
        // The dashboard UI is off: its index, asset, and SPA-fallback routes are not part of the API
        // contract, and the SPA catch-all would otherwise appear as a documented path.
        app.MapActa(
            "/acta",
            o =>
            {
                o.Enabled = false;
                o.EnableControls = true;
            }
        );
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;

        return await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
    }

    // Two kinds of line ending here, and both are platform-dependent. The file's own, and the ones
    // inside description strings: those come from XML doc comments, so they carry whatever the source
    // file had at checkout, escaped as a literal backslash-r backslash-n on Windows and backslash-n
    // on Linux. Normalizing only the first kind made the committed document unmatchable on the other
    // platform, which is how this gate passed locally and failed CI. Emission normalizes too, so the
    // file is deterministic rather than a record of which machine last regenerated it.
    private static string Normalize(string json) =>
        json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\r\\n", "\\n", StringComparison.Ordinal).TrimEnd();

    /// <summary>First differing line, so a failure names the drift instead of only announcing it.</summary>
    private static string FirstDifference(string committed, string generated)
    {
        var left = committed.Split('\n');
        var right = generated.Split('\n');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i].Trim() : "(end of file)";
            var b = i < right.Length ? right[i].Trim() : "(end of file)";
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return $"line {i + 1}. committed: {a} | generated: {b}.";
            }
        }
        return "no single line differs.";
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acta.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate Acta.slnx from " + AppContext.BaseDirectory);
    }
}
