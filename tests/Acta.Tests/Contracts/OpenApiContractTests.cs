using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.AspNetCore.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
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

    /// <summary>
    /// Integer-typed names the wire may carry. Every one is a value - a ledger position, a counter, a
    /// duration, a byte size, a policy number, a page size, an HTTP status - never an entity identity.
    /// The set is a literal, not a rule, so putting a new integer on the wire is a deliberate edit here
    /// that a reviewer sees; a new integer identity fails the gate on the day it is introduced.
    /// </summary>
    private static readonly HashSet<string> AllowedWireIntegers = new(StringComparer.Ordinal)
    {
        // Ledger positions, counters, and the concurrency token.
        "jobEventId",
        "executionNumber",
        "occurrenceCount",
        "failureCount",
        "retryCount",
        "version",
        // The CAS token a patch echoes back: the same counter as "version", read then re-sent.
        "expectedVersion",
        // Payload-format discriminators (a persisted-code number, not a row identity).
        "formatId",
        "inputFormatId",
        "outputFormatId",
        // Durations, byte sizes, and policy numbers, including their override/effective pairs.
        "byteLength",
        "durationMs",
        "deadlineSeconds",
        "delaySeconds",
        "deadlineSecondsEffective",
        "deadlineSecondsOverride",
        "executionTimeoutSeconds",
        "executionTimeoutSecondsEffective",
        "executionTimeoutSecondsOverride",
        "jobRetentionSeconds",
        "jobRetentionSecondsEffective",
        "jobRetentionSecondsOverride",
        "maxAttempts",
        "maxAttemptsEffective",
        "maxAttemptsOverride",
        "maxConcurrency",
        "oldestReadyAgeSeconds",
        "scheduleLagSeconds",
        // The operating-system process id a worker runs under: an external value, not an Acta row.
        "processId",
        // Paging and totals.
        "limit",
        "pageSize",
        "totalCount",
        "quarantineTotal",
        "schedulesTotal",
        "workersTotal",
        // Overview and outbox aggregate counts.
        "backlog",
        "deadWorkerCount",
        "dueSoonScheduleCount",
        "executingCount",
        "executorCapacity",
        "failedCount",
        "jobCount",
        "readyCount",
        "staleWorkerCount",
        "systemJobCount",
        "unresolvedAlertCount",
        "unresolvedCriticalAlertCount",
        // RFC 9457 problem details: the HTTP status code.
        "status",
    };

    /// <summary>
    /// Identity nouns 0.9.0 took off the wire. Banned by name whatever their type, because renaming one
    /// to a string would keep the database integer addressable and defeat the point of the refs cut.
    /// </summary>
    private static readonly string[] RetiredIdentityNames =
    [
        "jobId",
        "parentJobId",
        "lineageRootId",
        "workerId",
        "alertId",
        "definitionId",
        "tenantId",
        "namespaceId",
        "jobScheduleId",
        "leasedByWorkerId",
    ];

    // Structural gate, deliberately not a description grep: it walks the generated document itself, so
    // it sees what a client sees. Two independent checks - an integer outside the value allowlist, and a
    // retired identity noun anywhere - because either alone can be evaded by the other's shape.
    [Fact]
    public async Task No_integer_identities_in_openapi()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;
        var members = new List<WireMember>();

        foreach (var (schemaName, schema) in Members(document["components"]?["schemas"]))
        {
            CollectSchemaMembers(schema, schemaName, members);
        }

        foreach (var (path, operations) in Members(document["paths"]))
        {
            foreach (var placeholder in PathPlaceholders(path))
            {
                members.Add(new WireMember(placeholder, $"path {path}", IsInteger: false));
            }

            foreach (var (method, operation) in Members(operations))
            {
                var where = $"{method.ToUpperInvariant()} {path}";
                foreach (var parameter in Elements(operation?["parameters"]))
                {
                    if (parameter?["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name))
                    {
                        continue;
                    }
                    var schema = parameter["schema"];
                    members.Add(new WireMember(name, where, IsInteger(schema) || IsInteger(schema?["items"])));
                }

                foreach (var (_, media) in Members(operation?["requestBody"]?["content"]))
                {
                    CollectSchemaMembers(media?["schema"], $"{where} request", members);
                }

                foreach (var (statusCode, response) in Members(operation?["responses"]))
                {
                    foreach (var (_, media) in Members(response?["content"]))
                    {
                        CollectSchemaMembers(media?["schema"], $"{where} {statusCode}", members);
                    }
                }
            }
        }

        // A walker that found nothing would pass both checks vacuously; the document has hundreds of
        // members, so this pins that the traversal actually reached them.
        Assert.True(members.Count > 100, $"the document walk found only {members.Count} members; the traversal is broken.");

        var unexpectedIntegers = members
            .Where(m => m.IsInteger && !AllowedWireIntegers.Contains(m.Name))
            .Select(m => $"{m.Where}: integer '{m.Name}' is not an allowed wire value")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var retired = members
            .Where(m => RetiredIdentityNames.Contains(m.Name, StringComparer.Ordinal))
            .Select(m => $"{m.Where}: '{m.Name}' is a retired integer identity")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexpectedIntegers.Count == 0 && retired.Count == 0,
            "No database integer may be a wire identity. Address the entity by its public ref "
                + "(job_/alr_/wrk_) or its natural key; if the field really is a value, add it to "
                + "AllowedWireIntegers with the reason.\n\n"
                + string.Join("\n", unexpectedIntegers.Concat(retired))
        );
    }

    /// <summary>
    /// How <c>DashboardJsonContext</c> writes one enum member - the only authority on it, since that
    /// is the serializer every response body goes through. A member name the context cannot write is
    /// a fault in the caller, not a value to paper over, so it throws rather than passing the name
    /// along.
    /// </summary>
    private static string WireName(Type enumType, string memberName) =>
        JsonSerializer.Serialize(Enum.Parse(enumType, memberName), enumType, DashboardJsonContext.Default.Options).Trim('"');

    /// <summary>
    /// Every enum value the document declares is a value the server can actually write. The casing
    /// split this caught was invisible from either side alone: the response tests pin the wire and the
    /// drift gate pins the file, and both passed while a generated client would have rejected every
    /// enveloped response. This is the one fact that reads them against each other.
    /// </summary>
    [Fact]
    public async Task Declared_enum_values_are_the_ones_the_server_writes()
    {
        var document = JsonNode.Parse(await GenerateAsync())!;
        var declared = new List<(string Schema, string Value)>();
        foreach (var (name, schema) in Members(document["components"]?["schemas"]))
        {
            foreach (var value in Elements(schema?["enum"]))
            {
                declared.Add((name, value!.GetValue<string>()));
            }
        }

        // Enum lists are emitted only for the enums whose converter the generator cannot read, so a
        // walk that found none would pass by finding nothing to compare.
        Assert.True(declared.Count > 0, "the document declares no enum values; the walk is broken.");

        var wrong = declared
            .Where(d => !WritableValues(d.Schema).Contains(d.Value, StringComparer.Ordinal))
            .Select(d => $"{d.Schema}: declares '{d.Value}', which the server never writes")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            wrong.Count == 0,
            "A declared enum value the server does not write. The document is generated from the "
                + "endpoint graph but serialized through DashboardJsonContext, so a converter "
                + "registered on the context alone can spell a member one way on the wire and another "
                + "in the contract.\n\n"
                + string.Join("\n", wrong)
        );
    }

    /// <summary>The wire spellings of one named schema's enum, or an empty set when it is not an enum type.</summary>
    private static IReadOnlyList<string> WritableValues(string schemaName) =>
        typeof(IJobs)
            .Assembly.GetTypes()
            .Concat(typeof(DashboardJsonContext).Assembly.GetTypes())
            .FirstOrDefault(t => t.IsEnum && string.Equals(t.Name, schemaName, StringComparison.Ordinal))
            is { } type
            ? [.. Enum.GetNames(type).Select(name => WireName(type, name))]
            : [];

    /// <summary>One schema property, path placeholder, or operation parameter found in the document.</summary>
    private sealed record WireMember(string Name, string Where, bool IsInteger);

    // Walks one schema, naming every property it reaches. Composition keywords are followed so a member
    // cannot hide inside an allOf branch or an array's item schema; $ref is not followed because every
    // referenced component is walked in its own right.
    private static void CollectSchemaMembers(JsonNode? schema, string where, List<WireMember> members)
    {
        if (schema is not JsonObject node)
        {
            return;
        }

        foreach (var (name, property) in Members(node["properties"]))
        {
            members.Add(new WireMember(name, where, IsInteger(property)));
            CollectSchemaMembers(property, $"{where}.{name}", members);
        }

        CollectSchemaMembers(node["items"], $"{where}[]", members);
        CollectSchemaMembers(node["additionalProperties"], $"{where}{{}}", members);
        foreach (var keyword in CompositionKeywords)
        {
            foreach (var branch in Elements(node[keyword]))
            {
                CollectSchemaMembers(branch, where, members);
            }
        }
    }

    private static readonly string[] CompositionKeywords = ["allOf", "anyOf", "oneOf"];

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Members(JsonNode? node) =>
        node as JsonObject ?? Enumerable.Empty<KeyValuePair<string, JsonNode?>>();

    private static IEnumerable<JsonNode?> Elements(JsonNode? node) => node as JsonArray ?? Enumerable.Empty<JsonNode?>();

    // OpenAPI 3.1 renders a nullable integer as a type array, so both spellings have to be recognized.
    private static bool IsInteger(JsonNode? schema) =>
        schema?["type"] switch
        {
            JsonArray types => types.Any(IsIntegerLiteral),
            { } single => IsIntegerLiteral(single),
            _ => false,
        };

    private static bool IsIntegerLiteral(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, "integer", StringComparison.Ordinal);

    private static IEnumerable<string> PathPlaceholders(string path)
    {
        var start = path.IndexOf('{', StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = path.IndexOf('}', start);
            if (end < 0)
            {
                yield break;
            }
            yield return path[(start + 1)..end];
            start = path.IndexOf('{', end);
        }
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
        // Every optional member of a request record carries a C# default, and the generator copies
        // each one into the schema. They document nothing a reader needs - the absent case is already
        // "this field is optional" - and they make the committed document churn on a default nobody
        // reads, so the schema transformer below drops them all. (The two free-form payload fields
        // declare `JsonElement?` rather than a bare `JsonElement` for the same generator: an
        // uninitialized JsonElement has no value it can write as a default, and it throws there
        // before any transformer runs.)
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
            // An enum's declared value list came from the generator's own view of the type, and that
            // is not the view the server serializes with: responses are written through
            // DashboardJsonContext, whose converters spell the action enums camelCase while the
            // generator spelled them PascalCase. A value list in the wrong case is a contract a
            // generated client cannot match - it would reject every enveloped response this API
            // sends - so each value is re-read from the serializer that actually writes it. Applied
            // to every enum rather than the three that were wrong, so a new one cannot reintroduce
            // the split; the members are recased in place, so their declared order is untouched.
            o.AddSchemaTransformer(
                (schema, context, _) =>
                {
                    var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
                    if (schema.Enum is { Count: > 0 } && type.IsEnum)
                    {
                        schema.Enum = [.. schema.Enum.Select(value => (JsonNode)WireName(type, value!.GetValue<string>()))];
                    }
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
        var acta = app.MapActa(
            "/acta",
            o =>
            {
                o.Enabled = false;
                o.EnableControls = true;
            }
        );
        // The handlers read their JSON body through ControlEndpointValidation rather than a bound
        // parameter, so they declare it as RequestBodyDoc metadata (product-side, OpenAPI-free) and it
        // is translated into the framework's IAcceptsMetadata here - only here. In a serving host that
        // metadata is read by the routing matcher, which would start answering the missing-body and
        // wrong-content-type cases these handlers answer themselves; this host routes nothing but the
        // document read, so the translation is free. Finally() rather than Add() so it runs after the
        // per-endpoint conventions that write the declarations.
        ((IEndpointConventionBuilder)acta).Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<RequestBodyDoc>().LastOrDefault() is { } doc)
            {
                endpoint.Metadata.Add(new DeclaredRequestBody(doc));
            }
        });
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;

        return await app.GetTestClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
    }

    /// <summary>The framework-shaped body declaration the generator reads, built from a RequestBodyDoc.</summary>
    private sealed class DeclaredRequestBody(RequestBodyDoc doc) : IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes { get; } = ["application/json"];

        public Type? RequestType { get; } = doc.BodyType;

        public bool IsOptional { get; } = doc.Optional;
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
