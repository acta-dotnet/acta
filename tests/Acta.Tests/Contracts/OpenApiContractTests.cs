using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acta.AspNetCore;
using Acta.Sqlite;
using Acta.Tests.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Mount = Acta.Tests.Contracts.ActaOpenApiDocument.Mount;

namespace Acta.Tests.Contracts;

/// <summary>
/// Freezes the HTTP contract the way <c>PersistedCodeContractTests</c> freezes the code vocabulary: generate the document
/// from the real endpoint graph (<see cref="ActaOpenApiDocument"/>), compare it to the committed file, fail on any drift.
/// Regenerate deliberately with <c>ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests</c>, then
/// read the diff. The other facts hold the document to what the server reads and writes.
/// </summary>
public sealed partial class OpenApiContractTests
{
    private const string DocumentFile = "docs/reference/openapi.json";

    [Fact]
    public async Task Http_contract_matches_the_committed_document()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
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

    // Every operation carries a one-line summary, so a reader scanning the document never meets an unexplained action.
    [Fact]
    public async Task Every_operation_carries_a_summary()
    {
        var missing = Operations(await ActaOpenApiDocument.GenerateAsync())
            .Where(operation => string.IsNullOrWhiteSpace(operation.Value["summary"]?.GetValue<string>()))
            .Select(operation => operation.Key);

        Assert.Empty(missing);
    }

    /// <summary>
    /// Integer-typed names the wire may carry. Every one is a value - a ledger position, a counter, a duration, a byte
    /// size, a policy number, a page size, an HTTP status - never an entity identity. The set is a literal, so putting a
    /// new integer on the wire is a deliberate edit here that a reviewer sees.
    /// </summary>
    private static readonly HashSet<string> AllowedWireIntegers = new(StringComparer.Ordinal)
    {
        // Ledger positions, counters, and the concurrency token.
        "eventId",
        "executionNumber",
        // A step's attempt counter.
        "attemptNumber",
        // An event's subject id beside its entityKind: the subject outlives its row (a purged job's tombstone still
        // names it), so no ref can stand in for it.
        "entityId",
        "occurrenceCount",
        "failureCount",
        "retryCount",
        "outstandingCount",
        "version",
        // The CAS token a patch echoes back: the same counter as "version", read then re-sent.
        "expectedVersion",
        // A calendar family's content versions: the current, the latest, the one a read shows.
        "currentVersion",
        "latestVersion",
        "shownVersion",
        // How many schedules bind a calendar family: a count.
        "dependentCount",
        // The calendar verbs' CAS tokens and the draft to publish: content versions read, then re-sent.
        "expectedLatestVersion",
        "expectedCurrentVersion",
        "candidateVersion",
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
        "expireAfterSeconds",
        "jobRetentionSeconds",
        "jobRetentionSecondsEffective",
        "jobRetentionSecondsOverride",
        "maxAttempts",
        "maxAttemptsEffective",
        "maxAttemptsOverride",
        "concurrencyLimit",
        "concurrencyLimitEffective",
        "concurrencyLimitOverride",
        "maxConcurrentExecutors",
        "maxOutstanding",
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
        "overdueTotal",
        "workersTotal",
        // Overview and outbox aggregate counts.
        "backlog",
        "deadWorkerCount",
        "dueSoonScheduleCount",
        "executingCount",
        "executorCapacity",
        "failedCount",
        "readyCount",
        "staleWorkerCount",
        "unresolvedAlertCount",
        "unresolvedCriticalAlertCount",
        // RFC 9457 problem details: the HTTP status code.
        "status",
    };

    /// <summary>
    /// Identity nouns 0.9.0 took off the wire. Banned by name whatever their type, because renaming one to a string would
    /// keep the database integer addressable and defeat the point of the refs cut.
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

    // Structural, not a description grep: an integer outside the value allowlist, and a retired identity noun anywhere,
    // because either check alone can be evaded by the other's shape.
    [Fact]
    public async Task No_integer_identities_in_openapi()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
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

        // A walker that found nothing would pass both checks vacuously.
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
    /// The serializer reads and writes numbers as numbers (<c>{"expectedVersion":"3"}</c> is a 400), so no integer offers
    /// a string form or a digit pattern: a generator would otherwise wrap every integer in a union type.
    /// </summary>
    [Fact]
    public async Task No_integer_offers_a_string_form()
    {
        var unions = AllSchemas(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!)
            .Where(entry => IsInteger(entry.Schema) && (HasType(entry.Schema, "string") || entry.Schema["pattern"] is not null))
            .Select(entry => entry.Where)
            .ToList();

        Assert.True(unions.Count == 0, "Integers documented with a string form:\n" + string.Join("\n", unions));
    }

    [Fact]
    public async Task A_number_in_a_request_body_sent_as_a_string_is_refused()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{FakeJobs.FoundJobRef}/pause")
        {
            Content = new StringContent("""{"expectedVersion":"7"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Acta-Control", "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Every enum is an open string whose known values are exactly what the server writes, member by member, so an older
    /// client keeps a value it has not seen and compares against named constants for the ones it has. The confirmation
    /// header's one value is the only closed set: a protocol fact, not a vocabulary.
    /// </summary>
    [Fact]
    public async Task Every_enum_is_an_open_string_with_the_values_the_server_writes()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
        var open = new List<string>();
        var wrong = new List<string>();
        foreach (var (name, schema) in Members(document["components"]?["schemas"]))
        {
            if (schema?[ActaOpenApiDocument.KnownValuesExtension] is not JsonArray known)
            {
                if (EnumType(name) is not null)
                {
                    wrong.Add($"{name}: a code family without {ActaOpenApiDocument.KnownValuesExtension}");
                }
                continue;
            }

            open.Add(name);
            if (!HasType(schema, "string"))
            {
                wrong.Add($"{name}: an open code must be a string");
            }
            var declared = known.Select(v => $"{v!["name"]}={v["value"]}").ToList();
            if (EnumType(name) is not { } type)
            {
                wrong.Add($"{name}: known values with no enum to check them against");
            }
            else if (!declared.SequenceEqual(Enum.GetNames(type).Select(n => $"{n}={Written(type, n)}")))
            {
                wrong.Add($"{name}: declares [{string.Join(", ", declared)}], not what the server writes");
            }
        }

        var closed = AllSchemas(document).Where(entry => entry.Schema["enum"] is not null).Select(entry => entry.Where).ToList();

        Assert.Contains("JobStatusCode", open);
        Assert.Contains("ControlAction", open);
        Assert.Contains("EnqueueRejectionReason", open);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(["components.schemas.ActaControlConfirmation"], closed);
    }

    /// <summary>
    /// A code filter takes exactly the kebab code the server writes. The member name, an upper-case spelling, and the
    /// number each answer a 400 that names the parameter, on every route a code filter sits on.
    /// </summary>
    [Fact]
    public async Task A_code_filter_takes_only_its_code()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var filters = new List<string>();
        var wrong = new List<string>();
        foreach (
            var (key, operation) in Operations(generated).Where(operation => operation.Key.StartsWith("GET ", StringComparison.Ordinal))
        )
        {
            foreach (var parameter in Elements(operation["parameters"]))
            {
                if (
                    parameter?["schema"]?["$ref"]?.GetValue<string>() is not { } reference
                    || EnumType(reference.Split('/')[^1]) is not { } family
                )
                {
                    continue;
                }

                var name = parameter["name"]!.GetValue<string>();
                filters.Add(name);
                var member = Enum.GetValues(family).Cast<Enum>().First(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0);
                var code = ActaOpenApiDocument.WireName(family, member.ToString());
                var path = key[4..]
                    .Replace("{jobRef}", FakeJobs.FoundJobRef.ToString(), StringComparison.Ordinal)
                    .Replace("{jobNamespace}", "billing", StringComparison.Ordinal)
                    .Replace("{jobName}", "send-invoice", StringComparison.Ordinal);
                (string Value, HttpStatusCode Expected)[] spellings =
                [
                    (code, HttpStatusCode.OK),
                    (member.ToString(), HttpStatusCode.BadRequest),
                    (code.ToUpperInvariant(), HttpStatusCode.BadRequest),
                    (
                        Convert.ToInt64(member, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                        HttpStatusCode.BadRequest
                    ),
                ];
                foreach (var (value, expected) in spellings)
                {
                    var response = await client.GetAsync($"/acta/api{path}?{name}={Uri.EscapeDataString(value)}", ct);
                    var named =
                        response.StatusCode != HttpStatusCode.BadRequest
                        || (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["detail"]?.GetValue<string>() ?? "").Contains(
                            $"'{name}'",
                            StringComparison.Ordinal
                        );
                    if (response.StatusCode != expected || !named)
                    {
                        wrong.Add(
                            $"{key} {name}={value}: answered {(int)response.StatusCode}, expected {(int)expected} naming the parameter."
                        );
                    }
                }
            }
        }

        Assert.Contains("eventCode", filters);
        Assert.Contains("entityKind", filters);
        Assert.True(filters.Count > 10, $"only {filters.Count} code filters; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The refused enqueue's problem spells its reason itself; DashboardJsonContext writes every other enum.
    private static string Written(Type type, string member) =>
        type == typeof(EnqueueRejectionReason)
            ? ActaOpenApiDocument.ReasonCode(Enum.Parse<EnqueueRejectionReason>(member))
            : ActaOpenApiDocument.WireName(type, member);

    [Fact]
    public async Task A_refused_enqueue_writes_its_reason_as_the_document_spells_it()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var reason in Enum.GetValues<EnqueueRejectionReason>())
        {
            fakes.Jobs.Enqueue = (_, _) => throw new EnqueueRejectedException(reason, "refused");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/jobs")
            {
                Content = new StringContent("""{"jobNamespace":"billing","jobName":"send-invoice"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Acta-Control", "true");

            var response = await client.SendAsync(request, ct);
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ActaOpenApiDocument.ReasonCode(reason), problem["reasonCode"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// A component is never null itself; a nullable member says so where it uses one. Otherwise a non-null use reads as
    /// nullable in every generated client, and a null in a nullable use matches both branches of its oneOf.
    /// </summary>
    [Fact]
    public async Task No_component_allows_null()
    {
        var nullable = Members(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]?["schemas"])
            .Where(component => HasType(component.Value, "null"))
            .Select(component => component.Key)
            .ToList();

        Assert.True(nullable.Count == 0, "Components that allow null: " + string.Join(", ", nullable));
    }

    /// <summary>
    /// A control whose 409 is also a problem documents both shapes there; ControlEndpointTests proves the purge's problem.
    /// </summary>
    [Fact]
    public async Task A_conflict_that_is_also_a_problem_documents_both()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());

        Assert.All(
            ActaOpenApiDocument.ConflictAlsoProblem,
            key =>
            {
                var content = operations[key.Replace(" /", " /v1/", StringComparison.Ordinal)]["responses"]!["409"]!["content"]!;
                Assert.NotNull(content["application/json"]);
                Assert.NotNull(content["application/problem+json"]);
            }
        );
    }

    /// <summary>Every reference names a component the document carries; a dangling one fails only in a generator.</summary>
    [Fact]
    public async Task Every_reference_resolves()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
        var dangling = References(document)
            .Distinct(StringComparer.Ordinal)
            .Where(reference =>
                reference.Split('/') is not ["#", "components", var kind, var name] || document["components"]?[kind]?[name] is null
            )
            .ToList();

        Assert.True(dangling.Count == 0, "Dangling references: " + string.Join(", ", dangling));

        static IEnumerable<string> References(JsonNode? node) =>
            node switch
            {
                JsonObject members => members.SelectMany(member =>
                    member.Key == "$ref" && member.Value is JsonValue reference ? [reference.GetValue<string>()] : References(member.Value)
                ),
                JsonArray items => items.SelectMany(References),
                _ => [],
            };
    }

    /// <summary>
    /// A schema with no type says "any value"; only the members that really take any JSON value may say it: a job's
    /// input, the signal body, and the generator's component for them. A code or a ref rendered empty would type as
    /// <c>any</c> in every generated client.
    /// </summary>
    [Fact]
    public async Task No_empty_schema_outside_the_any_json_members()
    {
        string[] anyJson =
        [
            "components.schemas.JsonElement",
            "components.schemas.EnqueueJobRequest.input",
            "components.schemas.UpdateJobInputRequest.input",
        ];
        var empty = AllSchemas(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!)
            .Where(entry => entry.Schema.AsObject().All(p => p.Key is "description"))
            .Select(entry => entry.Where)
            .Where(where => !anyJson.Contains(where))
            .ToList();

        Assert.True(empty.Count == 0, "Empty schemas:\n" + string.Join("\n", empty));
    }

    /// <summary>
    /// A response says what the serializer does: a member it omits when null is optional and never null, and every other
    /// member is always present. A client that required an omitted member would throw on every response without it.
    /// </summary>
    [Fact]
    public async Task Response_members_are_required_unless_the_serializer_omits_them()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;

        var detail = schemas["GetJobDetailResponse"]!;
        Assert.DoesNotContain("result", Required(detail));
        Assert.Contains("job", Required(detail));
        Assert.Equal("#/components/schemas/JobPayloadBody", detail["properties"]!["result"]!["$ref"]!.GetValue<string>());

        var body = schemas["JobPayloadBody"]!;
        Assert.Equal(["formatName", "formatId"], Required(body));
        Assert.Equal("string", body["properties"]!["json"]!["type"]!.GetValue<string>());

        Assert.Contains("value", Required(schemas["ListTagsItem"]!));
    }

    /// <summary>
    /// A request member is required where the runtime refuses its absence and only there, and a request refuses unknown
    /// members. Each row of <see cref="ActaOpenApiDocument.HandlerRequiredMembers"/> is proven here: the route answers 400
    /// without it, on the fake host where the route refuses it and on a SQLite host where only the operation does.
    /// </summary>
    [Fact]
    public async Task Each_handler_required_member_is_refused_when_absent()
    {
        var found = FakeJobs.FoundJobRef;
        RequiredCase[] byRoute =
        [
            new("EnqueueJobRequest", "jobNamespace", HttpMethod.Post, "jobs", """{"jobName":"send-invoice"}"""),
            new("EnqueueJobRequest", "jobName", HttpMethod.Post, "jobs", """{"jobNamespace":"billing"}"""),
            new(
                "SaveCalendarDraftRequest",
                "definition",
                HttpMethod.Post,
                "calendars/billing/holidays/drafts",
                """{"role":"occurrence"}"""
            ),
            new("RegisterTenantRequest", "tenantName", HttpMethod.Post, "tenants", """{"displayName":"Acme"}"""),
            new("UpsertJobTagRequest", "name", HttpMethod.Post, $"jobs/{found}/tags", """{"value":"prod"}"""),
            new("UpsertNamespaceTagRequest", "name", HttpMethod.Post, "namespaces/billing/tags", """{"value":"prod"}"""),
            new("UpsertTenantTagRequest", "name", HttpMethod.Post, "tenants/acme/tags", """{"value":"prod"}"""),
            new("UpsertDefinitionTagRequest", "name", HttpMethod.Post, "definitions/billing/send-invoice/tags", """{"value":"prod"}"""),
            new("UpdateNamespaceRequest", "expectedVersion", HttpMethod.Patch, "namespaces/billing", """{"description":"d"}"""),
            new("UpdateTenantRequest", "expectedVersion", HttpMethod.Patch, "tenants/acme", """{"description":"d"}"""),
        ];
        RequiredCase[] byOperation =
        [
            new(
                "TagInput",
                "name",
                HttpMethod.Post,
                "jobs",
                $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo","tags":[{"value":"prod"}]}"""
            ),
            new(
                "SaveCalendarDraftRequest",
                "role",
                HttpMethod.Post,
                $"calendars/{SqliteNamespace}/holidays/drafts",
                """{"definition":{"kind":"dates"}}"""
            ),
            new("PublishCalendarRequest", "candidateVersion", HttpMethod.Post, $"calendars/{SqliteNamespace}/holidays/publish", "{}"),
        ];

        var refusals = new List<string>();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using (app)
        {
            await AssertRefusedAsync(client, byRoute, refusals);
        }
        await AssertRefusedOnSqliteAsync(byOperation, refusals);

        Assert.True(refusals.Count == 0, string.Join("\n", refusals));
        Assert.Equal(
            ActaOpenApiDocument
                .HandlerRequiredMembers.SelectMany(row => row.Value.Select(member => $"{row.Key}.{member}"))
                .Order(StringComparer.Ordinal),
            byRoute.Concat(byOperation).Select(c => $"{c.Schema}.{c.Member}").Order(StringComparer.Ordinal)
        );
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;
        Assert.Equal(["jobNamespace", "jobName"], Required(schemas["EnqueueJobRequest"]!));
        Assert.False(schemas["EnqueueJobRequest"]!["additionalProperties"]!.GetValue<bool>());
        Assert.False(schemas["PurgeJobRequest"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["status"], Required(schemas["ProblemDetails"]!));
        Assert.DoesNotContain("value", Required(schemas["UpsertJobTagRequest"]!));
        Assert.Equal(["priority"], Required(schemas["ReprioritizeJobRequest"]!));
    }

    private const string SqliteNamespace = "openapi-probe";

    private sealed record RequiredCase(string Schema, string Member, HttpMethod Method, string Path, string Body);

    // Sends each body, which leaves its case's member out, as a confirmed control, and records any answer but 400.
    private static async Task AssertRefusedAsync(HttpClient client, RequiredCase[] cases, List<string> refusals)
    {
        foreach (var (schema, member, method, path, body) in cases)
        {
            using var request = new HttpRequestMessage(method, "/acta/api/v1/" + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Acta-Control", "true");
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                refusals.Add($"{schema}.{member}: {method} {path} without it answered {response.StatusCode}.");
            }
        }
    }

    // A real Acta over a throwaway SQLite file, for the members only an operation refuses: the fakes accept anything.
    private static async Task AssertRefusedOnSqliteAsync(RequiredCase[] cases, List<string> refusals)
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-openapi-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.UseActa(j =>
        {
            j.UseSqlite(o =>
            {
                o.ConnectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
                o.ApplyMigrationsOnStartup = true;
            });
            j.Run<TestJobs.TestJobsManifest>(SqliteNamespace, ownerTeam: "test");
            j.DisableCli();
        });
        builder.Services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false);
        var app = builder.Build();
        app.MapActa("/acta", o => o.EnableControls = true);
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            await AssertRefusedAsync(app.GetTestClient(), cases, refusals);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex OperationIdShape();

    [Fact]
    public void An_operation_id_is_the_operations_name_under_any_mount()
    {
        Assert.Equal("getJob", OpenApiOperationIds.For("GET", "acta/api/v1/jobs/{jobRef}"));
        Assert.Equal("getJobByKey", OpenApiOperationIds.For("GET", "acta/api/v1/jobs/by-key"));
        Assert.Equal("pauseJob", OpenApiOperationIds.For("POST", "/ops/api/v1/jobs/{jobRef}/pause"));
        Assert.Equal("listEvents", OpenApiOperationIds.For("GET", "/v1/events"));
        Assert.Equal("listJobEvents", OpenApiOperationIds.For("GET", "/v1/jobs/{jobRef}/events"));
        Assert.Equal(
            "removeDefinitionTag",
            OpenApiOperationIds.For("DELETE", "api/v1/definitions/{jobNamespace}/{jobName}/tags/{tagName}")
        );
        Assert.Equal("POST /jobs/{jobRef}/purge", OpenApiOperationIds.Key("post", "acta/api/v1/jobs/{jobRef}/purge"));
        Assert.Throws<InvalidOperationException>(() => OpenApiOperationIds.For("GET", "acta/jobs"));
        Assert.Throws<InvalidOperationException>(() => OpenApiOperationIds.For("GET", "acta/api/v1/no-such-route"));
    }

    // OperationNameTests holds each id to its operation's name; here every operation of the document carries one, once.
    [Fact]
    public async Task Every_operation_has_a_unique_camel_case_id()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var ids = operations.Select(operation => operation.Value["operationId"]?.GetValue<string>() ?? "").ToList();

        Assert.True(ids.Count > 60, $"only {ids.Count} operations carry an id; the walk is broken.");
        Assert.All(ids, id => Assert.Matches(OperationIdShape(), id));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("pauseJob", operations["POST /v1/jobs/{jobRef}/pause"]["operationId"]!.GetValue<string>());
    }

    /// <summary>
    /// Paths read below the mount's API root, which the one server names: a client's base URL picks the mount, and the
    /// same generated client serves any mount.
    /// </summary>
    [Fact]
    public async Task Paths_sit_below_the_mount_api_root_the_server_names()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;

        Assert.Equal("/acta/api", Assert.Single(document["servers"]!.AsArray())!["url"]!.GetValue<string>());
        Assert.All(Members(document["paths"]), path => Assert.StartsWith("/v1/", path.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that mounts the API twice gets a document per mount: the read-only one lists its reads alone, with the ids
    /// the control mount's document gives the same operations, and only a mount that requires the confirmation header
    /// lists it on its controls.
    /// </summary>
    [Fact]
    public async Task Each_mount_documents_its_own_operations_with_shared_ids_and_its_own_confirmation()
    {
        var documents = await ActaOpenApiDocument.GenerateAsync(
            new Mount("/acta", Controls: true),
            new Mount("/ops", Controls: false),
            new Mount("/plain", Controls: true, RequireConfirmation: false)
        );
        var control = Operations(documents["/acta"]);
        var readOnly = Operations(documents["/ops"]);
        var unconfirmed = Operations(documents["/plain"]);

        Assert.Equal("/ops/api", JsonNode.Parse(documents["/ops"])!["servers"]![0]!["url"]!.GetValue<string>());
        Assert.All(readOnly.Keys, key => Assert.StartsWith("GET /v1/", key, StringComparison.Ordinal));
        Assert.True(readOnly.Count > 30 && readOnly.Count < control.Count, $"{readOnly.Count} reads against {control.Count} operations.");
        foreach (var (key, operation) in readOnly)
        {
            Assert.Equal(control[key]["operationId"]!.GetValue<string>(), operation["operationId"]!.GetValue<string>());
            Assert.Null(ConfirmationHeader(operation));
        }

        var header = JsonNode.Parse(documents["/acta"])!["components"]!["parameters"]!["ActaControl"]!;
        Assert.Equal(
            ("X-Acta-Control", "header", true),
            (header["name"]!.GetValue<string>(), header["in"]!.GetValue<string>(), header["required"]!.GetValue<bool>())
        );
        foreach (var (key, operation) in control)
        {
            if (key.StartsWith("GET ", StringComparison.Ordinal))
            {
                Assert.Null(ConfirmationHeader(operation));
                continue;
            }

            Assert.True(ConfirmationHeader(operation) is not null, $"{key}: a control lists no confirmation header.");
            Assert.Null(ConfirmationHeader(unconfirmed[key]));
        }
        Assert.Null(JsonNode.Parse(documents["/plain"])!["components"]!["parameters"]);
    }

    [Fact]
    public async Task The_enqueue_documents_its_location_header_on_both_successes_and_a_request_example()
    {
        var enqueue = Operations(await ActaOpenApiDocument.GenerateAsync())["POST /v1/jobs"];

        foreach (var code in new[] { "201", "200" })
        {
            Assert.NotNull(enqueue["responses"]![code]!["headers"]!["Location"]);
        }
        var example = enqueue["requestBody"]!["content"]!["application/json"]!["example"]!;
        Assert.Equal("send-invoice", example["jobName"]!.GetValue<string>());
    }

    /// <summary>The operations of one document keyed as <c>METHOD /path</c>.</summary>
    private static Dictionary<string, JsonNode> Operations(string json)
    {
        var operations = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (path, item) in Members(JsonNode.Parse(json)!["paths"]))
        {
            foreach (var (method, operation) in Members(item))
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!;
            }
        }
        return operations;
    }

    private static JsonNode? ConfirmationHeader(JsonNode operation) =>
        Elements(operation["parameters"]).FirstOrDefault(p => p?["$ref"]?.GetValue<string>() == "#/components/parameters/ActaControl");

    private static List<string> Required(JsonNode schema) => [.. Elements(schema["required"]).Select(n => n!.GetValue<string>())];

    private static Type? EnumType(string schemaName) =>
        typeof(IJobs)
            .Assembly.GetTypes()
            .Concat(typeof(DashboardJsonContext).Assembly.GetTypes())
            .FirstOrDefault(t => t.IsEnum && string.Equals(t.Name, schemaName, StringComparison.Ordinal));

    /// <summary>Every inline schema in the document, with where it sits: components, their members, and operation parameters.</summary>
    private static List<(string Where, JsonNode Schema)> AllSchemas(JsonNode document)
    {
        var found = new List<(string, JsonNode)>();
        foreach (var (name, schema) in Members(document["components"]?["schemas"]))
        {
            Walk(schema, $"components.schemas.{name}", found);
        }
        foreach (var (path, item) in Members(document["paths"]))
        {
            foreach (var (method, operation) in Members(item))
            {
                foreach (var parameter in Elements(operation?["parameters"]))
                {
                    Walk(parameter?["schema"], $"{method.ToUpperInvariant()} {path} {parameter?["name"]}", found);
                }
            }
        }
        return found;

        static void Walk(JsonNode? schema, string where, List<(string, JsonNode)> found)
        {
            if (schema is not JsonObject node || node.ContainsKey("$ref"))
            {
                return;
            }

            found.Add((where, node));
            foreach (var (name, property) in Members(node["properties"]))
            {
                Walk(property, $"{where}.{name}", found);
            }
            Walk(node["items"], $"{where}[]", found);
            foreach (var keyword in CompositionKeywords)
            {
                foreach (var branch in Elements(node[keyword]))
                {
                    Walk(branch, where, found);
                }
            }
        }
    }

    /// <summary>One schema property, path placeholder, or operation parameter found in the document.</summary>
    private sealed record WireMember(string Name, string Where, bool IsInteger);

    // Walks one schema, naming every property it reaches; composition keywords are followed, $ref is not, because every
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

    // OpenAPI 3.1 renders a nullable type as a type array, so both spellings are recognized.
    private static bool IsInteger(JsonNode? schema) => HasType(schema, "integer");

    private static bool HasType(JsonNode? schema, string type) =>
        schema?["type"] switch
        {
            JsonArray types => types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var text) && text == type),
            JsonValue single => single.TryGetValue<string>(out var text) && text == type,
            _ => false,
        };

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

    // Line endings, the file's and the escaped ones inside descriptions from XML doc comments, normalize on both sides so
    // the committed document matches on every platform.
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
