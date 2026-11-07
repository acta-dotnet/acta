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
        "inputFormatId",
        "resultFormatId",
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

    // The refused enqueue's problem spells its reason itself and the capabilities read its provider; DashboardJsonContext
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
    /// payload in its envelope, and the signal body, which sits in its operation. A code or a ref rendered empty would
    /// type as <c>any</c> in every generated client.
    /// </summary>
    [Fact]
    public async Task No_empty_schema_outside_the_any_json_members()
    {
        string[] anyJson = ["components.schemas.JobPayloadBody.json"];
        var schemas = AllSchemas(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!);
        var empty = schemas.Where(entry => entry.Schema.AsObject().All(p => p.Key is "description")).Select(entry => entry.Where).ToList();

        Assert.True(schemas.Count > 500, $"only {schemas.Count} schemas; the walk is broken.");
        Assert.Equal(anyJson.Order(StringComparer.Ordinal), empty.Where(anyJson.Contains).Order(StringComparer.Ordinal));
        Assert.True(empty.All(anyJson.Contains), "Empty schemas:\n" + string.Join("\n", empty.Where(where => !anyJson.Contains(where))));
    }

    /// <summary>
    /// Raw JSON is no type: the signal value and a JSON payload's envelope member are an inline schema that says any JSON
    /// value, with no component a client would generate a type for, and the server takes an object, an array, a string, a
    /// number, and a boolean in the signal, the enqueue's input, and the input update.
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
        ];

        var fakes = new FakeOperations();
        fakes.Jobs.StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{}"));
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;
        var answers = new List<string>();
        foreach (var value in new[] { """{"invoiceId":1}""", "[1,2]", "\"text\"", "42", "true" })
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
    /// The payload envelope holds the rules its description states, since <c>required</c> cannot say them: exactly one of
    /// json and base64, a formatId beside json that is JSON's, base64 with a binary formatId that decodes, and each readOnly
    /// member refused by name. The enqueue proves them, and the input update proves that base64 keeps the job's format.
    /// </summary>
    [Fact]
    public async Task The_payload_envelope_holds_the_rules_its_description_states()
    {
        var schema = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!["JobPayloadBody"]!;
        var readOnly = schema["properties"]!
            .AsObject()
            .Where(member => member.Value?["readOnly"]?.GetValue<bool>() == true)
            .Select(member => member.Key)
            .ToList();
        (string Envelope, string Refused)[] enqueues =
        [
            ("""{"json":{"invoiceId":1}}""", ""),
            ("""{"json":{"invoiceId":1},"formatId":1}""", ""),
            ("""{"base64":"AQID","formatId":2}""", ""),
            ("""{"base64":"AQID","formatId":128}""", ""),
            ("null", ""),
            ("""{"json":{"invoiceId":1},"formatId":2}""", "formatId"),
            ("""{"base64":"AQID"}""", "formatId"),
            ("""{"base64":"AQID","formatId":1}""", "formatId"),
            ("""{"base64":"AQID","formatId":3}""", "formatId"),
            ("""{"base64":"not base64!","formatId":2}""", "base64"),
            ("""{"json":1,"base64":"AQID","formatId":2}""", "exactly one"),
            ("{}", "exactly one"),
            .. readOnly.Select(member => ($$"""{"json":1,"{{member}}":{{ReadOnlySample[member]}}}""", $"input.{member} is read-only")),
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
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"json":1,"truncated":true}}""", HttpStatusCode.BadRequest, "input.truncated");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":200}}""", HttpStatusCode.OK, "");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":2}}""", HttpStatusCode.BadRequest, "formatId");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"json":{"invoiceId":2}}}""", HttpStatusCode.OK, "");
        await ExpectAsync(HttpMethod.Put, job, """{"input":{"base64":"AQID","formatId":2}}""", HttpStatusCode.BadRequest, "base64");

        Assert.Equal(ActaOpenApiDocument.EnvelopeResponseOnly, readOnly);
        Assert.Contains("exactly one of json and base64", schema["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(
            ["json 7B22696E766F6963654964223A317D", "json 7B22696E766F6963654964223A317D", "bytes 010203", "custom-128 010203", "none "],
            fakes.Jobs.EnqueueRequests.Select(request => $"{request.Input.Format.Name} {Convert.ToHexString(request.Input.Data.Span)}")
        );
        Assert.Equal(
            ["acme-binary/200 010203", "json/1 7B22696E766F6963654964223A327D"],
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

    /// <summary>A value of each readOnly envelope member's own type, so the refusal is the member's and not its type's.</summary>
    private static readonly Dictionary<string, string> ReadOnlySample = new(StringComparer.Ordinal)
    {
        ["formatName"] = "\"json\"",
        ["byteLength"] = "1",
        ["truncated"] = "true",
        ["malformed"] = "true",
    };

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

        Assert.DoesNotContain("input", Required(detail));
        Assert.Equal("#/components/schemas/JobPayloadBody", detail["properties"]!["input"]!["$ref"]!.GetValue<string>());

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
                """{"overrides":{}}""",
                """{"overrides":{},"expectedVersion":0}"""
            ),
            new(
                "UpdateDefinitionOverridesRequest",
                "overrides",
                HttpMethod.Put,
                "definitions/billing/send-invoice/overrides",
                """{"expectedVersion":0}""",
                """{"overrides":{},"expectedVersion":0}"""
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
            answers.Add(await SendControlAsync(client, HttpMethod.Put, definition + "/overrides", """{"overrides":{}}"""));
            answers.Add(
                await SendControlAsync(client, HttpMethod.Put, definition + "/overrides", """{"overrides":{},"expectedVersion":0}""")
            );
            answers.Add(await SendControlAsync(client, HttpMethod.Post, definition + "/retire", "{}"));
            answers.Add(await SendControlAsync(client, HttpMethod.Post, definition + "/retire", """{"expectedVersion":1}"""));
        });

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.OK], answers);
    }

    /// <summary>
    /// A full replace is a PUT: the tenant, namespace, and definition overrides updates clear what their body leaves out,
    /// so the document lists them under PUT and no PATCH, and the PATCH each had reaches no route.
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
    // the member and the second anything else, so the member and nothing beside it is what the route refuses.
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
            if (without.StatusCode != HttpStatusCode.BadRequest || !detail.Contains(member, StringComparison.OrdinalIgnoreCase))
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

    [Fact]
    public void An_operation_id_is_the_operations_name_under_any_mount()
    {
        Assert.Equal("getJob", OpenApiOperationIds.For("GET", "acta/api/v1/jobs/{jobRef}"));
        Assert.Equal("getJobByKey", OpenApiOperationIds.For("GET", "acta/api/v1/jobs/by-key"));
        Assert.Equal("pauseJob", OpenApiOperationIds.For("POST", "/ops/api/v1/jobs/{jobRef}/pause"));
        Assert.Equal("listEvents", OpenApiOperationIds.For("GET", "v1/api/v1/events"));
        Assert.Equal("listJobEvents", OpenApiOperationIds.For("GET", "/internal/acta/api/v1/jobs/{jobRef}/events"));
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

        var paths = Members(document["paths"]).ToList();

        Assert.Equal("/acta/api", Assert.Single(document["servers"]!.AsArray())!["url"]!.GetValue<string>());
        Assert.True(paths.Count > 50, $"only {paths.Count} paths; the walk is broken.");
        Assert.All(paths, path => Assert.StartsWith("/v1/", path.Key, StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that mounts the API twice gets a document per mount: the read-only one lists its reads alone, with the ids
    /// the control mount's document gives the same operations, and only a mount that requires the confirmation header
    /// lists it on its controls. A mount named like the version segment documents the same operations under the same ids.
    /// </summary>
    [Fact]
    public async Task Each_mount_documents_its_own_operations_with_shared_ids_and_its_own_confirmation()
    {
        var documents = await ActaOpenApiDocument.GenerateAsync(
            new Mount("/acta", Controls: true),
            new Mount("/ops", Controls: false),
            new Mount("/plain", Controls: true, RequireConfirmation: false),
            new Mount("/v1", Controls: true)
        );
        var control = Operations(documents["/acta"]);
        var readOnly = Operations(documents["/ops"]);
        var unconfirmed = Operations(documents["/plain"]);
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
        var example = enqueue["requestBody"]!["content"]!["application/json"]!["examples"]!["enqueueJob"]!["value"]!;
        Assert.Equal("send-invoice", example["jobName"]!.GetValue<string>());
    }

    /// <summary>
    /// Each JSON request body and each JSON 200 carries one example, the request the host over the staged fakes accepted
    /// and the body it answered (<see cref="OpenApiExamples"/>), and each is a value its schema allows, so an example that
    /// drifts from its schema fails here rather than in a reader's client.
    /// </summary>
    [Fact]
    public async Task Every_example_is_a_value_its_schema_allows()
    {
        var generated = await ActaOpenApiDocument.GenerateAsync();
        var schemas = JsonNode.Parse(generated)!["components"]!["schemas"]!.AsObject();
        var wrong = new List<string>();
        var examples = 0;
        foreach (var (key, operation) in Operations(generated))
        {
            foreach (
                var (where, media) in new[]
                {
                    ($"{key} request", operation["requestBody"]?["content"]?["application/json"]),
                    ($"{key} 200", operation["responses"]?["200"]?["content"]?["application/json"]),
                }
            )
            {
                if (media is null)
                {
                    continue;
                }
                if (media["examples"] is not JsonObject { Count: 1 } named)
                {
                    wrong.Add($"{where}: carries no single example.");
                    continue;
                }
                examples++;
                Describe(named.Single().Value!["value"], media["schema"]!, schemas, where, wrong);
            }
        }

        Assert.True(examples > 100, $"only {examples} examples; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
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
