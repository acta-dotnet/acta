using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var missing = operations
            .Where(operation => string.IsNullOrWhiteSpace(operation.Value["summary"]?.GetValue<string>()))
            .Select(operation => operation.Key);

        Assert.True(operations.Count > 60, $"only {operations.Count} operations; the walk is broken.");
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
        // Which business day of its period a business-day calendar fires on: an ordinal, 1 to 31.
        "nth",
        "inputFormatId",
        "resultFormatId",
        // Durations, byte sizes, and policy numbers, including their override/effective pairs.
        "byteLength",
        "durationMs",
        "deadlineSeconds",
        "delaySeconds",
        "deadlineSecondsEffective",
        "executionTimeoutSeconds",
        "executionTimeoutSecondsEffective",
        "expireAfterSeconds",
        "jobRetentionSeconds",
        "jobRetentionSecondsEffective",
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
        var integers = AllSchemas(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!)
            .Where(entry => IsInteger(entry.Schema))
            .ToList();
        var unions = integers
            .Where(entry => HasType(entry.Schema, "string") || entry.Schema["pattern"] is not null)
            .Select(entry => entry.Where)
            .ToList();

        Assert.True(integers.Count > 50, $"only {integers.Count} integer schemas; the walk is broken.");
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
    /// client keeps a value it has not seen and compares against named constants for the ones it has. No schema is a closed
    /// set.
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
            var declared = known.Select(v => v!.GetValue<string>()).ToList();
            if (EnumType(name) is not { } type)
            {
                wrong.Add($"{name}: known values with no enum to check them against");
            }
            else if (!declared.SequenceEqual(Enum.GetNames(type).Select(n => Written(type, n))))
            {
                wrong.Add($"{name}: declares [{string.Join(", ", declared)}], not what the server writes");
            }

            // Readable without the extension: the description names every value and says others may appear.
            var description = schema["description"]?.GetValue<string>() ?? "";
            if (
                !description.EndsWith(ActaOpenApiDocument.OtherValuesMayAppear, StringComparison.Ordinal)
                || declared.Any(value => !description.Contains(value, StringComparison.Ordinal))
                || schema["examples"] as JsonArray is not [JsonValue example]
                || !declared.Contains(example.GetValue<string>())
            )
            {
                wrong.Add($"{name}: its description or example does not show its known values");
            }
        }

        var closed = AllSchemas(document).Where(entry => entry.Schema["enum"] is not null).Select(entry => entry.Where).ToList();

        Assert.Contains("JobStatusCode", open);
        Assert.Contains("ControlAction", open);
        Assert.Contains("EnqueueRejectionReason", open);
        Assert.Contains("DbProvider", open);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Empty(closed);
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

    // The refused enqueue's problem spells its reason itself and the capabilities read its provider; ActaApiJsonContext
    // writes every other enum.
    private static string Written(Type type, string member) =>
        type == typeof(EnqueueRejectionReason) ? ActaOpenApiDocument.ReasonCode(Enum.Parse<EnqueueRejectionReason>(member))
        : type == typeof(DbProvider) ? GetCapabilitiesEndpoint.ProviderCode(Enum.Parse<DbProvider>(member))
        : ActaOpenApiDocument.WireName(type, member);

    /// <summary>The capabilities' provider is an open code: a host on each provider answers one of its known values.</summary>
    [Fact]
    public async Task The_capabilities_provider_is_one_of_its_known_values()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;
        var provider = schemas["GetCapabilitiesResponse"]!["properties"]!["provider"]!;
        var known = schemas["DbProvider"]![ActaOpenApiDocument.KnownValuesExtension]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        var answered = new List<string>();
        foreach (var each in Enum.GetValues<DbProvider>())
        {
            var fakes = new FakeOperations { Provider = each };
            var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
            await using (app)
            {
                var body = JsonNode.Parse(await client.GetStringAsync("/acta/api/v1/capabilities", TestContext.Current.CancellationToken))!;
                answered.Add(body["provider"]!.GetValue<string>());
            }
        }

        Assert.Equal("#/components/schemas/DbProvider", provider["$ref"]!.GetValue<string>());
        Assert.Equal(Enum.GetValues<DbProvider>().Length, answered.Distinct().Count());
        Assert.All(answered, value => Assert.Contains(value, known));
    }

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
        var components = Members(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]?["schemas"]).ToList();
        var nullable = components.Where(component => HasType(component.Value, "null")).Select(component => component.Key).ToList();

        Assert.True(components.Count > 100, $"only {components.Count} components; the walk is broken.");
        Assert.True(nullable.Count == 0, "Components that allow null: " + string.Join(", ", nullable));
    }

    /// <summary>Every reference names a component the document carries; a dangling one fails only in a generator.</summary>
    [Fact]
    public async Task Every_reference_resolves()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
        var references = References(document).Distinct(StringComparer.Ordinal).ToList();
        var dangling = references
            .Where(reference =>
                reference.Split('/') is not ["#", "components", var kind, var name] || document["components"]?[kind]?[name] is null
            )
            .ToList();

        Assert.True(references.Count > 100, $"only {references.Count} distinct references; the walk is broken.");
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
    /// A schema with no type says "any value"; only the members that really take any JSON value may say it: a JSON
    /// payload's json in a request and in a response, and the signal body, which sits in its operation. A code or a ref
    /// rendered empty would type as <c>any</c> in every generated client.
    /// </summary>
    [Fact]
    public async Task No_empty_schema_outside_the_any_json_members()
    {
        string[] anyJson = ["components.schemas.JobPayloadBody.json", "components.schemas.StoredJobPayload.json"];
        var schemas = AllSchemas(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!);
        var empty = schemas.Where(entry => entry.Schema.AsObject().All(p => p.Key is "description")).Select(entry => entry.Where).ToList();

        Assert.True(schemas.Count > 500, $"only {schemas.Count} schemas; the walk is broken.");
        Assert.Equal(anyJson.Order(StringComparer.Ordinal), empty.Where(anyJson.Contains).Order(StringComparer.Ordinal));
        Assert.True(empty.All(anyJson.Contains), "Empty schemas:\n" + string.Join("\n", empty.Where(where => !anyJson.Contains(where))));
    }

    /// <summary>
    /// Raw JSON is no type: the signal value and a JSON payload's json member, in a request and in a response, are an
    /// inline schema that says any JSON value, with no component a client would generate a type for, and the server takes
    /// an object, an array, a string, a number, a boolean, and null in the signal, the enqueue's input, and the input update.
    /// </summary>
    [Fact]
    public async Task Raw_json_is_an_inline_any_json_schema()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
        var schemas = JsonNode.Parse(generated)!["components"]!["schemas"]!;
        JsonNode[] anyJson =
        [
            Operations(generated)["POST /v1/jobs/{jobRef}/signals/{signalName}"]["requestBody"]!["content"]!["application/json"]![
                "schema"
            ]!,
            schemas["JobPayloadBody"]!["properties"]!["json"]!,
            schemas["StoredJobPayload"]!["properties"]!["json"]!,
        ];

        var fakes = new FakeOperations();
        fakes.Jobs.StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{}"));
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;
        var answers = new List<string>();
        foreach (var value in new[] { """{"invoiceId":1}""", "[1,2]", "\"text\"", "42", "true", "null" })
        {
            var job = FakeJobs.FoundJobRef;
            var enqueue = $$$"""{"jobNamespace":"billing","jobName":"send-invoice","input":{"json":{{{value}}}}}""";
            var update = $$$"""{"input":{"json":{{{value}}}}}""";
            answers.Add($"signal {value}: {await SendControlAsync(client, HttpMethod.Post, $"jobs/{job}/signals/approval", value)}");
            answers.Add($"enqueue {value}: {await SendControlAsync(client, HttpMethod.Post, "jobs", enqueue)}");
            answers.Add($"input {value}: {await SendControlAsync(client, HttpMethod.Put, $"jobs/{job}/input", update)}");
        }

        Assert.Null(schemas["JsonElement"]);
        Assert.All(anyJson, schema => Assert.Equal(["description"], schema.AsObject().Select(member => member.Key)));
        Assert.All(answers, answer => Assert.Matches(": (OK|Created)$", answer));
    }

    /// <summary>
    /// A request's payload holds the rules its schema's description states, since <c>required</c> cannot say them: exactly
    /// one of json and base64, a null json beside base64 counting as left out so a client that writes every member passes,
    /// json any JSON value with null included, a formatId beside json that is JSON's, and base64
    /// with a binary formatId that decodes. It requires nothing and refuses an unknown member, so each member only a stored
    /// payload carries is refused by name. The enqueue proves them, and the input update proves that base64 keeps the
    /// job's format.
    /// </summary>
    [Fact]
    public async Task The_payload_envelope_holds_the_rules_its_description_states()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;
        var schema = schemas["JobPayloadBody"]!;
        // A value of each member's own type, as a client echoing a response would send it.
        var responseOnly = schemas["StoredJobPayload"]!["properties"]!
            .AsObject()
            .Where(member => schema["properties"]![member.Key] is null)
            .Select(member =>
                (
                    member.Key,
                    Sample: HasType(member.Value, "integer") ? "1"
                    : HasType(member.Value, "boolean") ? "true"
                    : "\"json\""
                )
            )
            .ToList();
        (string Envelope, string Refused)[] enqueues =
        [
            ("""{"json":{"invoiceId":1}}""", ""),
            ("""{"json":{"invoiceId":1},"formatId":1}""", ""),
            ("""{"base64":"AQID","formatId":2}""", ""),
            ("""{"base64":"AQID","formatId":128}""", ""),
            ("""{"json":null}""", ""),
            ("""{"json":null,"base64":"AQID","formatId":2}""", ""),
            ("null", ""),
            ("""{"json":{"invoiceId":1},"formatId":2}""", "formatId"),
            ("""{"base64":"AQID"}""", "formatId"),
            ("""{"base64":"AQID","formatId":1}""", "formatId"),
            ("""{"base64":"AQID","formatId":3}""", "formatId"),
            ("""{"base64":"not base64!","formatId":2}""", "base64"),
            ("""{"json":1,"base64":"AQID","formatId":2}""", "exactly one"),
            ("{}", "exactly one"),
            .. responseOnly.Select(member => ($$"""{"json":1,"{{member.Key}}":{{member.Sample}}}""", $"'$.input.{member.Key}'")),
        ];
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;
        var wrong = new List<string>();
        foreach (var (envelope, refused) in enqueues)
        {
            var body = $$"""{"jobNamespace":"billing","jobName":"send-invoice","input":{{envelope}}}""";
            await ExpectAsync(
                HttpMethod.Post,
                "jobs",
                body,
                refused.Length == 0 ? HttpStatusCode.Created : HttpStatusCode.BadRequest,
                refused
            );
        }

        var job = $"jobs/{FakeJobs.FoundJobRef}/input";
        fakes.Jobs.StoredInput = JobPayload.FromBytes(JobPayloadFormat.Custom(200, "acme-binary"), [9]);
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":201}}""", HttpStatusCode.BadRequest, "formatId");
        await ExpectAsync(
            HttpMethod.Put,
            job,
            """{"input":{"json":1,"truncated":true}}""",
            HttpStatusCode.BadRequest,
            "'$.input.truncated'"
        );
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":200}}""", HttpStatusCode.OK, "");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":2}}""", HttpStatusCode.BadRequest, "formatId");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"json":{"invoiceId":2}}}""", HttpStatusCode.OK, "");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"json":null}}""", HttpStatusCode.OK, "");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":2}}""", HttpStatusCode.BadRequest, "base64");

        Assert.Equal(["formatName", "byteLength", "truncated", "malformed"], responseOnly.Select(member => member.Key));
        Assert.Empty(Required(schema));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Contains("exactly one of json", schema["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(
            [
                "json 7B22696E766F6963654964223A317D",
                "json 7B22696E766F6963654964223A317D",
                "bytes 010203",
                "custom-128 010203",
                "json 6E756C6C",
                "bytes 010203",
                "none ",
            ],
            fakes.Jobs.EnqueueRequests.Select(request => $"{request.Input.Format.Name} {Convert.ToHexString(request.Input.Data.Span)}")
        );
        Assert.Equal(
            ["acme-binary/200 010203", "json/1 7B22696E766F6963654964223A327D", "json/1 6E756C6C"],
            fakes.Jobs.InputAmendCalls.Select(input => $"{input.Format} {Convert.ToHexString(input.Data.Span)}")
        );

        async Task ExpectAsync(HttpMethod method, string path, string body, HttpStatusCode expected, string named)
        {
            using var response = await SendControlRequestAsync(client, method, path, body);
            var detail =
                expected == HttpStatusCode.BadRequest
                    ? JsonNode
                        .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                        ?["detail"]?.GetValue<string>()
                        ?? ""
                    : "";
            if (response.StatusCode != expected || !detail.Contains(named, StringComparison.Ordinal))
            {
                wrong.Add(
                    $"{method} {path} {body}: answered {(int)response.StatusCode} ({detail}), expected {(int)expected} naming '{named}'."
                );
            }
        }
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
        Assert.Equal("#/components/schemas/StoredJobPayload", detail["properties"]!["result"]!["$ref"]!.GetValue<string>());

        Assert.DoesNotContain("input", Required(detail));
        Assert.Equal("#/components/schemas/StoredJobPayload", detail["properties"]!["input"]!["$ref"]!.GetValue<string>());

        Assert.Equal(["formatId", "formatName"], Required(schemas["StoredJobPayload"]!).Order(StringComparer.Ordinal));
        Assert.Contains("value", Required(schemas["ListTagsItem"]!));
    }

    /// <summary>
    /// A request member is required where the runtime refuses its absence and only there, and a request refuses unknown
    /// members. Each row of <see cref="ActaOpenApiDocument.HandlerRequiredMembers"/> is proven here: the route answers 400
    /// without it and something else with it, on the fake host where the route refuses it and on a SQLite host where only
    /// the operation does.
    /// </summary>
    [Fact]
    public async Task Each_handler_required_member_is_refused_when_absent()
    {
        var found = FakeJobs.FoundJobRef;
        RequiredCase[] byRoute =
        [
            new(
                "EnqueueJobRequest",
                "jobNamespace",
                HttpMethod.Post,
                "jobs",
                """{"jobName":"send-invoice"}""",
                """{"jobNamespace":"billing","jobName":"send-invoice"}"""
            ),
            new(
                "EnqueueJobRequest",
                "jobName",
                HttpMethod.Post,
                "jobs",
                """{"jobNamespace":"billing"}""",
                """{"jobNamespace":"billing","jobName":"send-invoice"}"""
            ),
            new(
                "SaveCalendarDraftRequest",
                "definition",
                HttpMethod.Post,
                "calendars/billing/holidays/drafts",
                """{"role":"occurrence","expectedLatestVersion":0}""",
                """{"role":"occurrence","definition":{"kind":"dates"},"expectedLatestVersion":0}"""
            ),
            new(
                "RegisterTenantRequest",
                "tenantName",
                HttpMethod.Post,
                "tenants",
                """{"displayName":"Acme"}""",
                """{"tenantName":"acme","displayName":"Acme"}"""
            ),
            new(
                "UpdateJobInputRequest",
                "input",
                HttpMethod.Put,
                $"jobs/{found}/input",
                """{"reasonMessage":"fix"}""",
                """{"input":{"json":{"invoiceId":1}},"reasonMessage":"fix"}"""
            ),
            new(
                "UpdateNamespaceRequest",
                "expectedVersion",
                HttpMethod.Put,
                "namespaces/billing",
                """{"description":"d"}""",
                """{"description":"d","expectedVersion":3}"""
            ),
            new(
                "UpdateTenantRequest",
                "expectedVersion",
                HttpMethod.Put,
                "tenants/acme",
                """{"description":"d"}""",
                """{"description":"d","expectedVersion":0}"""
            ),
            new(
                "UpdateDefinitionOverridesRequest",
                "expectedVersion",
                HttpMethod.Put,
                "definitions/billing/send-invoice/overrides",
                $$"""{"overrides":{{NoOverrides}}}""",
                $$"""{"overrides":{{NoOverrides}},"expectedVersion":0}"""
            ),
            new(
                "UpdateDefinitionOverridesRequest",
                "overrides",
                HttpMethod.Put,
                "definitions/billing/send-invoice/overrides",
                """{"expectedVersion":0}""",
                $$"""{"overrides":{{NoOverrides}},"expectedVersion":0}"""
            ),
            new(
                "RetireDefinitionRequest",
                "expectedVersion",
                HttpMethod.Post,
                "definitions/billing/send-invoice/retire",
                "{}",
                """{"expectedVersion":0}"""
            ),
            new(
                "SaveCalendarDraftRequest",
                "expectedLatestVersion",
                HttpMethod.Post,
                "calendars/billing/holidays/drafts",
                """{"role":"occurrence","definition":{"kind":"dates"}}""",
                """{"role":"occurrence","definition":{"kind":"dates"},"expectedLatestVersion":0}"""
            ),
            new(
                "PublishCalendarRequest",
                "expectedCurrentVersion",
                HttpMethod.Post,
                "calendars/billing/holidays/publish",
                """{"candidateVersion":1}""",
                """{"candidateVersion":1,"expectedCurrentVersion":0}"""
            ),
            new(
                "PublishCalendarRequest",
                "candidateVersion",
                HttpMethod.Post,
                "calendars/billing/holidays/publish",
                """{"expectedCurrentVersion":0}""",
                """{"candidateVersion":1,"expectedCurrentVersion":0}"""
            ),
            new(
                "ArchiveCalendarRequest",
                "expectedCurrentVersion",
                HttpMethod.Post,
                "calendars/billing/holidays/archive",
                "{}",
                """{"expectedCurrentVersion":1}"""
            ),
        ];
        RequiredCase[] byOperation =
        [
            new(
                "TagInput",
                "name",
                HttpMethod.Post,
                "jobs",
                $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo","tags":[{"value":"prod"}]}""",
                $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo","tags":[{"name":"env","value":"prod"}]}"""
            ),
            new(
                "SaveCalendarDraftRequest",
                "role",
                HttpMethod.Post,
                $"calendars/{SqliteNamespace}/holidays/drafts",
                """{"definition":{"kind":"dates"},"expectedLatestVersion":0}""",
                """{"role":"occurrence","definition":{"kind":"dates"},"expectedLatestVersion":0}"""
            ),
        ];

        var refusals = new List<string>();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using (app)
        {
            await AssertRefusedAsync(client, byRoute, refusals);
        }
        await WithSqliteHostAsync(client => AssertRefusedAsync(client, byOperation, refusals));

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
        Assert.Empty(Required(schemas["UpsertTagRequest"]!));
        Assert.Equal(["priority"], Required(schemas["ReprioritizeJobRequest"]!));
    }

    /// <summary>
    /// A definition nobody has edited is at version 0, its first real version, so an overrides update or a retire that
    /// leaves the version out is refused rather than matching it, and one that sends the version applies.
    /// </summary>
    [Fact]
    public async Task An_omitted_expected_version_never_matches_a_definition()
    {
        var answers = new List<HttpStatusCode>();
        await WithSqliteHostAsync(async client =>
        {
            var definition = $"definitions/{SqliteNamespace}/add-numbers";
            answers.Add(await SendControlAsync(client, HttpMethod.Put, definition + "/overrides", $$"""{"overrides":{{NoOverrides}}}"""));
            answers.Add(
                await SendControlAsync(
                    client,
                    HttpMethod.Put,
                    definition + "/overrides",
                    $$"""{"overrides":{{NoOverrides}},"expectedVersion":0}"""
                )
            );
            answers.Add(await SendControlAsync(client, HttpMethod.Post, definition + "/retire", "{}"));
            answers.Add(await SendControlAsync(client, HttpMethod.Post, definition + "/retire", """{"expectedVersion":1}"""));
        });

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.OK], answers);
    }

    /// <summary>The members of a definition's overrides, as the wire names them.</summary>
    private static readonly string[] OverrideMembers =
    [
        "priority",
        "maxAttempts",
        "concurrencyLimit",
        "rateLimit",
        "backoff",
        "executionTimeoutSeconds",
        "deadlineSeconds",
        "deadlineBehavior",
        "jobRetentionSeconds",
        "eventRecording",
        "alertProfile",
        "alertChannelName",
        "runbookUrl",
        "displayName",
        "description",
    ];

    public static TheoryData<string> OverrideMemberCases => [.. OverrideMembers];

    /// <summary>Every override null: the overrides an update sends to clear them all.</summary>
    private static readonly string NoOverrides = JsonSerializer.Serialize(
        new DefinitionPolicyOverrides(),
        ActaApiJsonContext.Default.DefinitionPolicyOverrides
    );

    /// <summary>
    /// The definition read and the overrides update share one overrides schema: it requires every member, each still
    /// nullable, refuses unknown ones, and the read carries no override beside it.
    /// </summary>
    [Fact]
    public async Task The_definition_read_and_the_overrides_update_share_one_overrides_schema()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;
        var overrides = schemas["DefinitionPolicyOverrides"]!;
        var read = schemas["GetDefinitionResponse"]!["properties"]!;
        static bool Nullable(JsonNode member) =>
            Elements(member["type"])
                .Concat(Elements(member["oneOf"]).Select(branch => branch?["type"]))
                .Any(type => type?.GetValue<string>() == "null");

        Assert.Equal(OverrideMembers.Order(StringComparer.Ordinal), Required(overrides).Order(StringComparer.Ordinal));
        Assert.All(OverrideMembers, member => Assert.True(Nullable(overrides["properties"]![member]!), $"{member} is not nullable."));
        Assert.False(overrides["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("#/components/schemas/DefinitionPolicyOverrides", read["overrides"]!["$ref"]!.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/DefinitionPolicyOverrides",
            schemas["UpdateDefinitionOverridesRequest"]!["properties"]!["overrides"]!["$ref"]!.GetValue<string>()
        );
        Assert.Contains("overrides", Required(schemas["GetDefinitionResponse"]!));
        Assert.DoesNotContain(read.AsObject(), member => member.Key.EndsWith("Override", StringComparison.Ordinal));
    }

    /// <summary>
    /// The overrides update takes every override, so a body that leaves one out answers 400 naming it, where it would
    /// otherwise clear that override.
    /// </summary>
    [Theory]
    [MemberData(nameof(OverrideMemberCases))]
    public async Task An_overrides_update_that_leaves_an_override_out_is_refused_naming_it(string member)
    {
        var overrides = JsonNode.Parse(NoOverrides)!.AsObject();
        Assert.True(overrides.Remove(member));
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using var _ = app;

        using var response = await SendControlRequestAsync(
            client,
            HttpMethod.Put,
            "definitions/billing/send-invoice/overrides",
            $$"""{"overrides":{{overrides.ToJsonString()}},"expectedVersion":1}"""
        );
        var detail = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))?["detail"];

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            $"The control request body leaves out '$.overrides.{member}', which this endpoint requires.",
            detail?.GetValue<string>()
        );
    }

    /// <summary>A body that carries every override, nulls included, is applied with exactly the overrides it carries.</summary>
    [Fact]
    public async Task An_overrides_update_that_carries_every_override_is_applied()
    {
        var fakes = new FakeOperations();
        var received = new List<DefinitionPolicyOverrides>();
        var update = fakes.Definitions.UpdateOverrides;
        fakes.Definitions.UpdateOverrides = (jobNamespace, jobName, expectedVersion, overrides, reason, actor, ct) =>
        {
            received.Add(overrides);
            return update(jobNamespace, jobName, expectedVersion, overrides, reason, actor, ct);
        };
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;
        var sent = new DefinitionPolicyOverrides(MaxAttempts: 5, RateLimit: "10/s", DeadlineBehavior: DeadlineBehaviorCode.Advisory);
        var overrides = JsonSerializer.Serialize(sent, ActaApiJsonContext.Default.DefinitionPolicyOverrides);

        var status = await SendControlAsync(
            client,
            HttpMethod.Put,
            "definitions/billing/send-invoice/overrides",
            $$"""{"overrides":{{overrides}},"expectedVersion":1}"""
        );

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(sent, Assert.Single(received));
    }

    /// <summary>
    /// The definition read answers its overrides in the shape the update takes, each in its own member: the overrides an
    /// update stored read back as sent, and that read, sent back as it is, is applied and changes no override and no value
    /// in force.
    /// </summary>
    [Fact]
    public async Task A_definitions_overrides_read_back_and_sent_back_change_nothing()
    {
        const string stored = """
            {"priority":"high","maxAttempts":7,"concurrencyLimit":4,"rateLimit":"10/s","backoff":"5s..9m x4 +-20%",
            "executionTimeoutSeconds":33,"deadlineSeconds":444,"deadlineBehavior":"advisory","jobRetentionSeconds":555,
            "eventRecording":"audit","alertProfile":"on-terminal","alertChannelName":"ops-pager",
            "runbookUrl":"https://runbooks.example.com/add-numbers","displayName":"Add numbers","description":"Adds two numbers."}
            """;
        var reads = new List<JsonNode>();
        var answers = new List<HttpStatusCode>();
        await WithSqliteHostAsync(async client =>
        {
            var definition = $"definitions/{SqliteNamespace}/add-numbers";
            async Task<JsonNode> ReadAsync() =>
                JsonNode.Parse(await client.GetStringAsync("/acta/api/v1/" + definition, TestContext.Current.CancellationToken))!;
            async Task UpdateAsync(string overrides, JsonNode version) =>
                answers.Add(
                    await SendControlAsync(
                        client,
                        HttpMethod.Put,
                        definition + "/overrides",
                        $$"""{"overrides":{{overrides}},"expectedVersion":{{version.ToJsonString()}}}"""
                    )
                );

            await UpdateAsync(stored, (await ReadAsync())["version"]!);
            reads.Add(await ReadAsync());
            await UpdateAsync(reads[0]["overrides"]!.ToJsonString(), reads[0]["version"]!);
            reads.Add(await ReadAsync());
        });
        static JsonObject InForce(JsonNode read) =>
            new(
                read.AsObject()
                    .Where(member => member.Key.EndsWith("Effective", StringComparison.Ordinal))
                    .Select(member => KeyValuePair.Create(member.Key, member.Value?.DeepClone()))
            );

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], answers);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stored), reads[0]["overrides"]), reads[0]["overrides"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(reads[0]["overrides"], reads[1]["overrides"]), reads[1]["overrides"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(InForce(reads[0]), InForce(reads[1])), InForce(reads[1]).ToJsonString());
        Assert.Equal(15, InForce(reads[0]).Count);
    }

    /// <summary>
    /// A calendar read embeds its definition as the JSON object the draft took, its text the stored bytes: compact, the keys
    /// in the order sent, each escape as the store wrote it. Both reads carry it, the document types it as a calendar definition, and
    /// the read's definition, sent back as it is, saves the next version with the same content.
    /// </summary>
    [Fact]
    public async Task A_calendar_read_embeds_its_stored_definition_which_a_draft_takes_back()
    {
        const string sent = """
            { "timeZone" : "UTC", "kind" : "daily-local-time", "description" : "Caf\u00e9 <closed>", "times" : [ "09:00" ] }
            """;
        var compiled = CalendarDefinition.Compile(CalendarRoleCode.Occurrence, sent);
        var stored = Encoding.UTF8.GetString(compiled.CanonicalJson.Span);
        var schema = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!["GetCalendarResponse"]!;
        var drafts = new List<string>();
        var definitions = new List<string>();
        await WithSqliteHostAsync(async client =>
        {
            var ct = TestContext.Current.CancellationToken;
            var family = $"calendars/{SqliteNamespace}/pay-days";
            async Task DraftAsync(string definition, int expectedLatestVersion)
            {
                using var answer = await SendControlRequestAsync(
                    client,
                    HttpMethod.Post,
                    family + "/drafts",
                    $$"""{"role":"occurrence","definition":{{definition}},"expectedLatestVersion":{{expectedLatestVersion}}}"""
                );
                drafts.Add($"{(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync(ct)}");
            }
            async Task<JsonElement> ReadAsync(string path)
            {
                using var read = JsonDocument.Parse(await client.GetStringAsync("/acta/api/v1/" + path, ct));
                return read.RootElement.Clone();
            }

            await DraftAsync(sent, 0);
            var head = await ReadAsync(family);
            var detail = await ReadAsync(family + "/detail");
            definitions.Add(head.GetProperty("definition").GetRawText());
            definitions.Add(detail.GetProperty("calendar").GetProperty("definition").GetRawText());
            await DraftAsync(definitions[0], head.GetProperty("latestVersion").GetInt32());
        });
        Assert.Equal([stored, stored], definitions);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sent), JsonNode.Parse(definitions[0])), definitions[0]);
        Assert.Equal("#/components/schemas/CalendarDefinition", schema["properties"]!["definition"]!["$ref"]!.GetValue<string>());
        Assert.Equal(
            [
                $$"""200 {"action":"applied","version":1,"contentHash":"{{compiled.ContentHash}}","problems":[]}""",
                $$"""200 {"action":"applied","version":2,"contentHash":"{{compiled.ContentHash}}","problems":[]}""",
            ],
            drafts
        );
    }

    /// <summary>
    /// A full replace is a PUT: the tenant and namespace updates clear what their body leaves out, and the definition
    /// overrides update takes every override, so the document lists them under PUT and no PATCH, and the PATCH each had
    /// reaches no route.
    /// </summary>
    [Fact]
    public async Task A_full_replace_is_a_put()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using var _ = app;
        var patches = new List<HttpStatusCode>();
        foreach (var path in new[] { "tenants/cust-001", "namespaces/billing", "definitions/billing/send-invoice" })
        {
            patches.Add(await SendControlAsync(client, HttpMethod.Patch, path, """{"expectedVersion":1,"overrides":{}}"""));
        }

        Assert.DoesNotContain(operations.Keys, key => key.StartsWith("PATCH ", StringComparison.Ordinal));
        Assert.Equal("updateTenant", operations["PUT /v1/tenants/{tenantName}"]["operationId"]!.GetValue<string>());
        Assert.Equal("updateNamespace", operations["PUT /v1/namespaces/{jobNamespace}"]["operationId"]!.GetValue<string>());
        Assert.Equal(
            "updateDefinitionOverrides",
            operations["PUT /v1/definitions/{jobNamespace}/{jobName}/overrides"]["operationId"]!.GetValue<string>()
        );
        Assert.All(patches, status => Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{status}"));
    }

    /// <summary>
    /// A tag is upserted by its name: one PUT per owner, the name in the path normalized as every tag name is, and a body
    /// of the value alone, so a name in the body is an unknown member. The POST that took the name in its body is gone.
    /// </summary>
    [Fact]
    public async Task A_tag_is_upserted_by_its_name()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
        var operations = Operations(generated);
        var schema = JsonNode.Parse(generated)!["components"]!["schemas"]!["UpsertTagRequest"]!;
        string[] owners = [$"jobs/{SqliteJob}", $"namespaces/{SqliteNamespace}", "tenants/acme", $"definitions/{SqliteNamespace}/echo"];
        var answers = new List<string>();
        await WithSqliteHostAsync(async client =>
        {
            var ct = TestContext.Current.CancellationToken;
            await SendControlAsync(client, HttpMethod.Post, "tenants", """{"tenantName":"acme"}""");
            using var enqueued = await SendControlRequestAsync(
                client,
                HttpMethod.Post,
                "jobs",
                $$"""{"jobNamespace":"{{SqliteNamespace}}","jobName":"echo","deduplicationKey":"tag-probe"}"""
            );
            var jobRef = JsonNode.Parse(await enqueued.Content.ReadAsStringAsync(ct))!["jobRef"]!.GetValue<string>();
            foreach (var owner in owners.Select(owner => owner.Replace(SqliteJob, jobRef, StringComparison.Ordinal)))
            {
                var upsert = await SendControlAsync(client, HttpMethod.Put, $"{owner}/tags/Env.Region", """{"value":"EU West"}""");
                using var named = await SendControlRequestAsync(
                    client,
                    HttpMethod.Put,
                    $"{owner}/tags/env",
                    """{"name":"env","value":"x"}"""
                );
                var post = await SendControlAsync(client, HttpMethod.Post, $"{owner}/tags", """{"name":"env","value":"x"}""");
                var tags = JsonNode.Parse(await client.GetStringAsync($"/acta/api/v1/{owner}/tags", ct))!["items"]!.AsArray();
                var detail = JsonNode.Parse(await named.Content.ReadAsStringAsync(ct))?["detail"]?.GetValue<string>() ?? "";
                var postGone = post is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
                answers.Add(
                    $"{(int)upsert} {(int)named.StatusCode} {detail.Contains("'$.name'", StringComparison.Ordinal)} {postGone} "
                        + string.Join(",", tags.Select(tag => $"{tag!["name"]!.GetValue<string>()}={tag["value"]!.GetValue<string>()}"))
                );
            }
        });

        Assert.Equal(Enumerable.Repeat("200 400 True True env.region=EU West", owners.Length), answers);
        Assert.Equal(["value"], schema["properties"]!.AsObject().Select(member => member.Key));
        Assert.Equal(
            ["upsertJobTag", "upsertNamespaceTag", "upsertTenantTag", "upsertDefinitionTag"],
            TagOwnerRoutes.Select(owner => operations[$"PUT /v1/{owner}/tags/{{tagName}}"]["operationId"]!.GetValue<string>())
        );
        Assert.DoesNotContain(
            operations.Keys,
            key => key.StartsWith("POST ", StringComparison.Ordinal) && key.EndsWith("/tags", StringComparison.Ordinal)
        );
    }

    private static readonly string[] TagOwnerRoutes =
    [
        "jobs/{jobRef}",
        "namespaces/{jobNamespace}",
        "tenants/{tenantName}",
        "definitions/{jobNamespace}/{jobName}",
    ];

    private const string SqliteNamespace = "openapi-probe";

    private const string SqliteJob = "{job}";

    /// <summary>One required member: a body without it, and the same body with it.</summary>
    private sealed record RequiredCase(string Schema, string Member, HttpMethod Method, string Path, string Absent, string Present);

    // Sends each case's body without its member, then with it, as confirmed controls: the first must answer a 400 naming
    // the member and the second anything else, so the member and nothing beside it is what the route refuses. The detail
    // names the member as a whole word, so a member never matches inside a longer name: its wire name where the route
    // refuses it, or its C# name where an operation's argument check does (request.Tags[0].Name).
    private static async Task AssertRefusedAsync(HttpClient client, RequiredCase[] cases, List<string> refusals)
    {
        foreach (var (schema, member, method, path, absent, present) in cases)
        {
            using var without = await SendControlRequestAsync(client, method, path, absent);
            var detail =
                JsonNode
                    .Parse(await without.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                    ?["detail"]?.GetValue<string>()
                ?? "";
            var named = $@"\b(?:{Regex.Escape(member)}|{Regex.Escape(char.ToUpperInvariant(member[0]) + member[1..])})\b";
            if (without.StatusCode != HttpStatusCode.BadRequest || !Regex.IsMatch(detail, named))
            {
                refusals.Add($"{schema}.{member}: {method} {path} without it answered {(int)without.StatusCode} ({detail}).");
            }
            if (await SendControlAsync(client, method, path, present) == HttpStatusCode.BadRequest)
            {
                refusals.Add($"{schema}.{member}: {method} {path} with it answered 400 too.");
            }
        }
    }

    private static async Task<HttpStatusCode> SendControlAsync(HttpClient client, HttpMethod method, string path, string body)
    {
        using var response = await SendControlRequestAsync(client, method, path, body);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> SendControlRequestAsync(HttpClient client, HttpMethod method, string path, string body)
    {
        using var request = new HttpRequestMessage(method, "/acta/api/v1/" + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Acta-Control", "true");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // A real Acta over a throwaway SQLite file, for what only an operation refuses: the fakes accept anything.
    private static async Task WithSqliteHostAsync(Func<HttpClient, Task> act)
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
            await act(app.GetTestClient());
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

    /// <summary>
    /// Every operation of the committed document names the one permission it needs in <c>x-acta-permission</c>: every read
    /// needs <c>acta.read</c> and nothing else does, and the enqueue alone needs <c>acta.enqueue</c>.
    /// </summary>
    [Fact]
    public void Every_operation_names_its_permission_and_only_a_read_needs_read()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), DocumentFile.Replace('/', Path.DirectorySeparatorChar)));
        var operations = Operations(committed)
            .Select(operation =>
                (
                    operation.Key,
                    Id: operation.Value["operationId"]?.GetValue<string>(),
                    Permission: operation.Value[ActaOpenApiDocument.PermissionExtension]?.GetValue<string>()
                )
            )
            .ToList();
        string[] permissions = [ActaPermissions.Read, ActaPermissions.Enqueue, ActaPermissions.Control, ActaPermissions.Admin];

        Assert.True(operations.Count > 60, $"only {operations.Count} operations; the walk is broken.");
        Assert.All(operations, operation => Assert.Contains(operation.Permission, permissions));
        Assert.All(
            operations,
            operation =>
                Assert.True(
                    operation.Key.StartsWith("GET ", StringComparison.Ordinal) == (operation.Permission == ActaPermissions.Read),
                    $"{operation.Key} needs {operation.Permission}."
                )
        );
        Assert.Equal("enqueueJob", Assert.Single(operations, operation => operation.Permission == ActaPermissions.Enqueue).Id);
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

        var paths = Members(document["paths"]).ToList();

        Assert.Equal("/acta/api", Assert.Single(document["servers"]!.AsArray())!["url"]!.GetValue<string>());
        Assert.True(paths.Count > 50, $"only {paths.Count} paths; the walk is broken.");
        Assert.All(paths, path => Assert.StartsWith("/v1/", path.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that mounts the API twice gets a document per mount: the read-only one lists its reads alone, with the ids
    /// the control mount's document gives the same operations. No operation lists the confirmation as a parameter: a mount
    /// whose controls require it adds the rule to its introduction, with its own header name, and a mount without controls
    /// or with confirmation off does not. A mount named like the version segment documents the same operations under the
    /// same ids.
    /// </summary>
    [Fact]
    public async Task Each_mount_documents_its_own_operations_with_shared_ids_and_its_own_confirmation()
    {
        var documents = await ActaOpenApiDocument.GenerateAsync(
            new Mount("/acta", Controls: true),
            new Mount("/ops", Controls: false),
            new Mount("/plain", Controls: true, RequireConfirmation: false),
            new Mount("/v1", Controls: true, ConfirmationHeaderName: "X-Ops-Confirm")
        );
        var control = Operations(documents["/acta"]);
        var readOnly = Operations(documents["/ops"]);
        var versionNamed = Operations(documents["/v1"]);

        Assert.Equal("/ops/api", JsonNode.Parse(documents["/ops"])!["servers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal("/v1/api", JsonNode.Parse(documents["/v1"])!["servers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(
            control.Select(operation => $"{operation.Key} {operation.Value["operationId"]}").Order(StringComparer.Ordinal),
            versionNamed.Select(operation => $"{operation.Key} {operation.Value["operationId"]}").Order(StringComparer.Ordinal)
        );
        Assert.All(readOnly.Keys, key => Assert.StartsWith("GET /v1/", key, StringComparison.Ordinal));
        Assert.True(readOnly.Count > 30 && readOnly.Count < control.Count, $"{readOnly.Count} reads against {control.Count} operations.");
        foreach (var (key, operation) in readOnly)
        {
            Assert.Equal(control[key]["operationId"]!.GetValue<string>(), operation["operationId"]!.GetValue<string>());
        }

        foreach (var document in documents.Values)
        {
            Assert.Null(JsonNode.Parse(document)!["components"]!["parameters"]);
            Assert.DoesNotContain("ActaControl", document, StringComparison.Ordinal);
            Assert.All(
                Operations(document),
                operation =>
                    Assert.DoesNotContain(
                        Elements(operation.Value["parameters"]),
                        parameter => parameter?["in"]?.GetValue<string>() == "header"
                    )
            );
        }

        var plain = Introduction(documents["/plain"]);
        Assert.Equal(plain, Introduction(documents["/ops"]));
        Assert.DoesNotContain("cross-site", plain, StringComparison.Ordinal);
        Assert.Equal(
            plain
                + "\n\nA call that changes something must show that it is no cross-site form post, in one of three ways: the header "
                + "X-Acta-Control: true, a JSON content type, or a Bearer authorization header. A client that sends a JSON body or a "
                + "bearer token passes without doing anything more; for a call with no body and no bearer token, send the header.",
            Introduction(documents["/acta"])
        );
        Assert.Equal(
            Introduction(documents["/acta"]).Replace("X-Acta-Control", "X-Ops-Confirm", StringComparison.Ordinal),
            Introduction(documents["/v1"])
        );
    }

    private static string Introduction(string document) => JsonNode.Parse(document)!["info"]!["description"]!.GetValue<string>();

    [Fact]
    public async Task The_enqueue_documents_its_location_header_on_both_successes_and_a_request_example()
    {
        var enqueue = Operations(await ActaOpenApiDocument.GenerateAsync())["POST /v1/jobs"];

        foreach (var code in new[] { "201", "200" })
        {
            Assert.NotNull(enqueue["responses"]![code]!["headers"]!["Location"]);
        }
        var example = enqueue["requestBody"]!["content"]!["application/json"]!["examples"]!["enqueueJob"]!["value"]!;
        Assert.Equal("send-invoice", example["jobName"]!.GetValue<string>());
    }

    /// <summary>
    /// Each JSON request body and each JSON 200 carries one example, the request the host over the staged fakes accepted
    /// and the body it answered (<see cref="OpenApiExamples"/>), as does the success of a route whose success is not a 200,
    /// such as an outbox verb's 202. Each example present, whatever its status, is a value its schema allows, so an
    /// example that drifts from its schema fails here rather than in a reader's client.
    /// </summary>
    [Fact]
    public async Task Every_example_is_a_value_its_schema_allows()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
        var schemas = JsonNode.Parse(generated)!["components"]!["schemas"]!.AsObject();
        var wrong = new List<string>();
        var checkedAt = new List<string>();
        foreach (var (key, operation) in Operations(generated))
        {
            (string Where, JsonNode? Media, bool Expected)[] bodies =
            [
                ($"{key} request", operation["requestBody"]?["content"]?["application/json"], true),
                .. Members(operation["responses"])
                    .Select(response => ($"{key} {response.Key}", response.Value?["content"]?["application/json"], response.Key == "200")),
            ];
            foreach (var (where, media, expected) in bodies)
            {
                if (media is null || (media["examples"] is null && !expected))
                {
                    continue;
                }
                if (media["examples"] is not JsonObject { Count: 1 } named)
                {
                    wrong.Add($"{where}: carries no single example.");
                    continue;
                }
                checkedAt.Add(where);
                Describe(named.Single().Value!["value"], media["schema"]!, schemas, where, wrong);
            }
        }

        Assert.True(checkedAt.Count > 100, $"only {checkedAt.Count} examples; the walk is broken.");
        Assert.Contains("POST /v1/outbox/{jobNamespace}/requeue 202", checkedAt);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// Every instant an example carries is one the server writes: UTC, so it ends in Z or an offset, and a year after
    /// 2000. A staged instant left at its default reads 0001-01-01T00:00:00, with no zone, which no stored row holds.
    /// </summary>
    [Fact]
    public async Task Every_example_instant_names_its_zone_and_a_real_year()
    {
        var instants = 0;
        var wrong = new List<string>();
        foreach (var (key, operation) in Operations(await ActaOpenApiDocument.GenerateAsync()))
        {
            var media = Members(operation["requestBody"]?["content"])
                .Concat(Members(operation["responses"]).SelectMany(response => Members(response.Value?["content"])))
                .Select(content => content.Value);
            foreach (var text in media.SelectMany(m => Members(m?["examples"])).SelectMany(example => Strings(example.Value?["value"])))
            {
                if (InstantText().Match(text) is not { Success: true } instant)
                {
                    continue;
                }
                instants++;
                if (!instant.Groups["zone"].Success || int.Parse(instant.Groups["year"].Value, CultureInfo.InvariantCulture) < 2000)
                {
                    wrong.Add($"{key}: {text}");
                }
            }
        }

        Assert.True(instants > 20, $"only {instants} instants in the examples; the walk is broken.");
        Assert.True(wrong.Count == 0, "Instants with no zone or before 2000:\n" + string.Join("\n", wrong));

        static IEnumerable<string> Strings(JsonNode? node) =>
            node switch
            {
                JsonObject members => members.SelectMany(member => Strings(member.Value)),
                JsonArray items => items.SelectMany(Strings),
                JsonValue value when value.TryGetValue<string>(out var text) => [text],
                _ => [],
            };
    }

    [GeneratedRegex(@"^(?<year>\d{4})-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(?<zone>Z|[+-]\d{2}:\d{2})?$")]
    private static partial Regex InstantText();

    /// <summary>
    /// A published request is one the real operations take, not only the staged fakes: the checks each operation runs
    /// in process before it reaches the database accept it. The calendar draft's definition compiles, the enqueue passes
    /// the enqueue's validation, the overrides pass the override checks, and the tenant and namespace fields pass theirs.
    /// </summary>
    [Fact]
    public void Each_request_example_passes_its_operation_own_checks()
    {
        static T Body<T>(string key, JsonTypeInfo<T> info) => JsonSerializer.Deserialize(OpenApiExamples.Requests[key].Trim(), info)!;
        var draft = Body("POST /calendars/{jobNamespace}/{calendarName}/drafts", ActaApiJsonContext.Default.SaveCalendarDraftRequest);
        var enqueue = Body("POST /jobs", ActaApiJsonContext.Default.EnqueueJobRequest);
        var overrides = Body(
            "PUT /definitions/{jobNamespace}/{jobName}/overrides",
            ActaApiJsonContext.Default.UpdateDefinitionOverridesRequest
        );
        var register = Body("POST /tenants", ActaApiJsonContext.Default.RegisterTenantRequest);
        var tenant = Body("PUT /tenants/{tenantName}", ActaApiJsonContext.Default.UpdateTenantRequest);
        var ns = Body("PUT /namespaces/{jobNamespace}", ActaApiJsonContext.Default.UpdateNamespaceRequest);

        IReadOnlyList<string> problems = Record.Exception(() =>
            CalendarDefinition.Compile(draft.Role, draft.Definition.GetRawText())
        ) switch
        {
            null => [],
            CalendarDefinitionException refused => refused.Problems,
            var other => [other.Message],
        };
        Action[] checks =
        [
            () =>
                JobEnqueueRequestValidation.NormalizeAndValidate(
                    new Acta.EnqueueJobRequest(
                        JobNamespace: enqueue.JobNamespace!,
                        JobName: enqueue.JobName!,
                        Input: enqueue.Input!.ToPayload("input"),
                        DeduplicationKey: enqueue.DeduplicationKey,
                        CorrelationKey: enqueue.CorrelationKey,
                        Priority: enqueue.Priority,
                        NextRunAtUtc: enqueue.NextRunAtUtc,
                        DelaySeconds: enqueue.DelaySeconds,
                        TenantName: enqueue.TenantName,
                        Lane: enqueue.Lane,
                        ConcurrencyKey: enqueue.ConcurrencyKey,
                        Tags: enqueue.Tags
                    ),
                    "request"
                ),
            () => UpdateDefinitionOverridesHandler.Normalize(overrides.Overrides!),
            () => NameSyntax.Canonicalize(register.TenantName!, "tenantName", NameSyntax.ExtendedMaxLength, reserveSystem: true),
            () => CatalogValidation.ValidateTenant(register.DisplayName, register.Description),
            () => CatalogValidation.ValidateTenant(tenant.DisplayName, tenant.Description),
            () => CatalogValidation.ValidateNamespace(ns.OwnerTeam, ns.Description),
        ];

        Assert.Equal(CalendarRoleCode.Availability, draft.Role);
        Assert.Empty(problems);
        Assert.All(checks, check => Assert.Null(Record.Exception(check)));
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

    private static List<string> Required(JsonNode schema) => [.. Elements(schema["required"]).Select(n => n!.GetValue<string>())];

    private static Type? EnumType(string schemaName) =>
        typeof(IJobs)
            .Assembly.GetTypes()
            .Concat(typeof(ActaApiJsonContext).Assembly.GetTypes())
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

    // The file's line endings normalize on both sides so the committed document matches on every platform.
    private static string Normalize(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

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
