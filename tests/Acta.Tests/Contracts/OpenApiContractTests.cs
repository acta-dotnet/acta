using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            await File.WriteAllTextAsync(path, generated, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"{DocumentFile} missing: run with ACTA_EMIT_OPENAPI=1.");
        var committed = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            Normalize(committed) == Normalize(generated),
            $"{DocumentFile} is stale. The HTTP surface changed. Regenerate: "
                + "ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests"
        );
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
            o.AddSchemaTransformer(
                (schema, _, _) =>
                {
                    schema.Default = null;
                    return Task.CompletedTask;
                }
            )
        );

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

    private static string Normalize(string json) => json.Replace("\r\n", "\n").TrimEnd();

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
