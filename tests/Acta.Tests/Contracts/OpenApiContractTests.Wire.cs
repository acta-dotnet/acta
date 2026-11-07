using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.Tests.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed partial class OpenApiContractTests
{
    /// <summary>
    /// Every operation lists 401 and 403, because every route can be refused before it runs: a host that requires
    /// authentication answers 401, and the local-only guard answers a remote caller 403. Each operation is asked both.
    /// </summary>
    [Fact]
    public async Task Every_operation_declares_401_and_403_and_answers_them()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var wrong = operations
            .Where(operation => operation.Value["responses"]?["401"] is null || operation.Value["responses"]?["403"] is null)
            .Select(operation => $"{operation.Key}: does not declare both 401 and 403.")
            .ToList();

        var (remote, remoteClient) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureApp: app =>
                app.Use(
                    (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
                        return next(context);
                    }
                )
        );
        await using (remote)
        {
            await AssertAnswerAsync(remoteClient, operations, HttpStatusCode.Forbidden, wrong);
        }

        var (guarded, guardedClient) = await TestDashboardHost.StartAsync(
            options =>
            {
                options.EnableControls = true;
                options.ConfigureEndpoints = group => group.RequireAuthorization();
            },
            configureBuilder: builder =>
            {
                builder.Services.AddAuthentication("Anonymous").AddScheme<AuthenticationSchemeOptions, AnonymousHandler>("Anonymous", null);
                builder.Services.AddAuthorization();
            }
        );
        await using (guarded)
        {
            await AssertAnswerAsync(guardedClient, operations, HttpStatusCode.Unauthorized, wrong);
        }

        Assert.True(operations.Count > 60, $"only {operations.Count} operations; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // Sends every operation and adds to wrong each that answers another status; a 403 carries the problem document the
    // operation declares for it.
    private static async Task AssertAnswerAsync(
        HttpClient client,
        Dictionary<string, JsonNode> operations,
        HttpStatusCode expected,
        List<string> wrong
    )
    {
        foreach (var (key, operation) in operations)
        {
            using var response = await SendOperationAsync(client, key, operation);
            if (response.StatusCode != expected)
            {
                wrong.Add($"{key}: answered {(int)response.StatusCode}, expected {(int)expected}.");
            }
            else if (
                expected == HttpStatusCode.Forbidden
                && JsonNode
                    .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                    ?["status"]?.GetValue<int>() != (int)expected
            )
            {
                wrong.Add($"{key}: the 403 is not a problem document naming its status.");
            }
        }
    }

    // One operation keyed METHOD /path, its route values and required query filled from OpenApiExamples.RouteValues, with the
    // confirmation header unless confirm is false.
    private static async Task<HttpResponseMessage> SendOperationAsync(
        HttpClient client,
        string key,
        JsonNode operation,
        HttpContent? content = null,
        bool confirm = true
    )
    {
        var space = key.IndexOf(' ', StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(key[..space]), "/acta/api" + ReadUrl(key[(space + 1)..], operation))
        {
            Content = content,
        };
        if (confirm)
        {
            request.Headers.Add("X-Acta-Control", "true");
        }
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static bool Declares(JsonNode operation, HttpStatusCode status) =>
        operation["responses"]?[((int)status).ToString(CultureInfo.InvariantCulture)] is not null;

    /// <summary>
    /// A route declares 413 and 415 exactly where it reads a body: there an oversized body is the 413 and a text/plain one
    /// the 415, and every other route answers neither.
    /// </summary>
    [Fact]
    public async Task Only_a_route_that_reads_a_body_declares_413_and_415()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var (app, client) = await TestDashboardHost.StartAsync(
            options => options.EnableControls = true,
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 64)
        );
        await using var _ = app;
        var wrong = new List<string>();
        foreach (var (key, operation) in operations)
        {
            var readsBody = operation["requestBody"] is not null;
            using var oversized = await SendOperationAsync(
                client,
                key,
                operation,
                new StringContent($$"""{"reasonMessage":"{{new string('a', 100)}}"}""", Encoding.UTF8, "application/json")
            );
            using var text = await SendOperationAsync(client, key, operation, new StringContent("x", Encoding.UTF8, "text/plain"));
            foreach (
                var (status, answered) in new[]
                {
                    (HttpStatusCode.RequestEntityTooLarge, oversized),
                    (HttpStatusCode.UnsupportedMediaType, text),
                }
            )
            {
                if (Declares(operation, status) != readsBody || (answered.StatusCode == status) != readsBody)
                {
                    wrong.Add(
                        $"{key}: reads a body {readsBody}, declares {(int)status} {Declares(operation, status)}, answered {(int)answered.StatusCode}."
                    );
                }
            }
        }

        Assert.True(operations.Count(operation => operation.Value["requestBody"] is not null) > 30, "too few bodies; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A route declares 400 exactly where a request can be malformed. On the real operations each read that declares it
    /// answers it for a route key that cannot name anything, a query value of no type, or a required one left out, and a
    /// read none of those can reach declares none; each control answers it without its confirmation header.
    /// </summary>
    [Fact]
    public async Task A_route_declares_400_where_a_request_can_be_malformed()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var ct = TestContext.Current.CancellationToken;
        var wrong = new List<string>();
        await WithSqliteHostAsync(async client =>
        {
            foreach (var (key, operation) in operations.Where(operation => operation.Key.StartsWith("GET ", StringComparison.Ordinal)))
            {
                var answered = new List<string>();
                foreach (var url in MalformedReads(key[4..], operation))
                {
                    if ((await client.GetAsync("/acta/api" + url, ct)).StatusCode == HttpStatusCode.BadRequest)
                    {
                        answered.Add(url);
                    }
                }
                if (Declares(operation, HttpStatusCode.BadRequest) != answered.Count > 0)
                {
                    wrong.Add(
                        $"{key}: declares 400 {Declares(operation, HttpStatusCode.BadRequest)}, answered it to [{string.Join(", ", answered)}]."
                    );
                }
            }
        });

        var (app, fakeClient) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        await using (app)
        {
            foreach (var (key, operation) in operations.Where(operation => !operation.Key.StartsWith("GET ", StringComparison.Ordinal)))
            {
                using var response = await SendOperationAsync(fakeClient, key, operation, confirm: false);
                if (!Declares(operation, HttpStatusCode.BadRequest) || response.StatusCode != HttpStatusCode.BadRequest)
                {
                    wrong.Add($"{key}: without its confirmation header answered {(int)response.StatusCode}.");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // A read with each route key in turn naming nothing it could address, with each query parameter in turn given a value
    // of no type, and without its required query parameters.
    private static IEnumerable<string> MalformedReads(string path, JsonNode operation)
    {
        var url = ReadUrl(path, operation);
        foreach (var name in PathPlaceholders(path))
        {
            yield return ReadUrl(path.Replace($"{{{name}}}", Uri.EscapeDataString("Not A Key!"), StringComparison.Ordinal), operation);
        }
        foreach (var parameter in Elements(operation["parameters"]).Where(parameter => parameter?["in"]?.GetValue<string>() == "query"))
        {
            var name = parameter!["name"]!.GetValue<string>();
            yield return $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{name}={Uri.EscapeDataString("Not A Value!")}";
        }
        yield return url.Split('?')[0];
    }

    /// <summary>
    /// A read declares 503 exactly where it reads the database: over facades whose every asynchronous call times out, each
    /// such read answers 503, and a read that touches no database answers as it would with one.
    /// </summary>
    [Fact]
    public async Task A_read_declares_503_where_it_reads_the_database()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var (app, client) = await TestDashboardHost.StartAsync(configureBuilder: UnreachableDatabase.Register);
        await using var _ = app;
        var wrong = new List<string>();
        var reads = operations.Where(operation => operation.Key.StartsWith("GET ", StringComparison.Ordinal)).ToList();
        foreach (var (key, operation) in reads)
        {
            using var response = await SendOperationAsync(client, key, operation);
            if (Declares(operation, HttpStatusCode.ServiceUnavailable) != (response.StatusCode == HttpStatusCode.ServiceUnavailable))
            {
                wrong.Add(
                    $"{key}: declares 503 {Declares(operation, HttpStatusCode.ServiceUnavailable)}, answered {(int)response.StatusCode}."
                );
            }
        }

        Assert.True(reads.Count > 30, $"only {reads.Count} reads; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A list's pageSize and a preview's limit declare their least value and their default, and say their cap, above which
    /// the server clamps rather than refuses. On the real operations an omitted value takes the declared default and a
    /// larger one the cap, and 0, below the declared minimum, is a 400 naming the parameter.
    /// </summary>
    [Fact]
    public async Task Paging_bounds_are_declared_and_held()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var caps = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["pageSize"] = JobsQueryLimits.MaxPageSize,
            ["limit"] = NextOccurrenceCalculator.MaxWalk,
        };
        var bounded = new List<(string Key, JsonNode Operation, string Name, int Default)>();
        var wrong = new List<string>();
        foreach (var (key, operation) in operations)
        {
            foreach (
                var parameter in Elements(operation["parameters"])
                    .Where(p => p?["name"]?.GetValue<string>() is { } n && caps.ContainsKey(n))
            )
            {
                var name = parameter!["name"]!.GetValue<string>();
                var schema = parameter["schema"]!;
                var description = parameter["description"]?.GetValue<string>() ?? "";
                if (
                    schema["minimum"]?.GetValue<int>() != 1
                    || schema["default"] is null
                    || !description.Contains($"at most {caps[name]}", StringComparison.Ordinal)
                )
                {
                    wrong.Add($"{key} {name}: declares {parameter.ToJsonString()}");
                    continue;
                }
                bounded.Add((key, operation, name, schema["default"]!.GetValue<int>()));
            }
        }

        var held = new List<string>();
        await WithSqliteHostAsync(async client =>
        {
            var ct = TestContext.Current.CancellationToken;
            await SendControlAsync(
                client,
                HttpMethod.Post,
                $"calendars/{SqliteNamespace}/daily/drafts",
                """{"role":"occurrence","definition":{"kind":"daily-local-time","timeZone":"UTC","times":["09:00"]},"expectedLatestVersion":0}"""
            );
            var values = new Dictionary<string, string>(OpenApiExamples.RouteValues, StringComparer.Ordinal)
            {
                ["jobNamespace"] = SqliteNamespace,
                ["jobName"] = "recurring-ping",
                ["scheduleName"] = "every-5-minutes",
                ["calendarName"] = "daily",
            };
            foreach (var (key, operation, name, declaredDefault) in bounded)
            {
                var path = key[4..];
                var required = Elements(operation["parameters"])
                    .Where(p => p?["in"]?.GetValue<string>() == "query" && p["required"]?.GetValue<bool>() == true)
                    .Select(p => $"{p!["name"]!.GetValue<string>()}={values[p["name"]!.GetValue<string>()]}&");
                var url =
                    "/acta/api"
                    + PathPlaceholders(path).Aggregate(path, (filled, n) => filled.Replace($"{{{n}}}", values[n], StringComparison.Ordinal))
                    + "?"
                    + string.Concat(required);
                using var omitted = await client.GetAsync(url, ct);
                // A list under a target this host does not hold is skipped; the lists that answer prove the paging they share.
                if (omitted.StatusCode != HttpStatusCode.OK && name == "pageSize")
                {
                    continue;
                }
                using var above = await client.GetAsync($"{url}{name}={caps[name] * 10}", ct);
                var answered = (Size(await omitted.Content.ReadAsStringAsync(ct)), Size(await above.Content.ReadAsStringAsync(ct)));
                if (answered != (declaredDefault, caps[name]))
                {
                    wrong.Add($"{key} {name}: omitted and above the cap answered {answered}, not ({declaredDefault}, {caps[name]}).");
                }
                using var zero = await client.GetAsync($"{url}{name}=0", ct);
                var detail = JsonNode.Parse(await zero.Content.ReadAsStringAsync(ct))?["detail"]?.GetValue<string>() ?? "";
                if (zero.StatusCode != HttpStatusCode.BadRequest || !detail.Contains(name, StringComparison.Ordinal))
                {
                    wrong.Add($"{key}: a {name} of 0 answered {(int)zero.StatusCode} ({detail}), not a 400 naming it.");
                }
                held.Add(key);
            }

            // A page says its size; a forecast is as long as it says.
            static int? Size(string body) =>
                JsonNode.Parse(body) is { } node
                    ? node["pageSize"]?.GetValue<int>() ?? (node["nextOccurrencesUtc"] ?? node["occurrencesUtc"])?.AsArray().Count
                    : null;
        });

        Assert.Contains(
            held,
            key => key.EndsWith("/preview", StringComparison.Ordinal) && key.Contains("/schedules/", StringComparison.Ordinal)
        );
        Assert.Contains(
            held,
            key => key.EndsWith("/preview", StringComparison.Ordinal) && key.Contains("/calendars/", StringComparison.Ordinal)
        );
        Assert.True(held.Count > 12, $"only {held.Count} bounded reads answered; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A numeric id filter declares its least value, 1, because the server refuses a lower one: on the real operations an
    /// entityId of 0 answers 400.
    /// </summary>
    [Fact]
    public async Task A_numeric_id_filter_declares_its_minimum_of_1()
    {
        var ids = Operations(await ActaOpenApiDocument.GenerateAsync())
            .SelectMany(operation => Elements(operation.Value["parameters"]).Select(parameter => (operation.Key, Parameter: parameter!)))
            .Where(entry =>
                entry.Parameter["in"]?.GetValue<string>() == "query"
                && IsInteger(entry.Parameter["schema"])
                && entry.Parameter["name"]!.GetValue<string>().EndsWith("Id", StringComparison.Ordinal)
            )
            .ToList();
        var answered = HttpStatusCode.OK;
        await WithSqliteHostAsync(async client =>
        {
            using var response = await client.GetAsync(
                "/acta/api/v1/events?entityKind=job&entityId=0",
                TestContext.Current.CancellationToken
            );
            answered = response.StatusCode;
        });

        Assert.Equal(["GET /v1/events entityId"], ids.Select(id => $"{id.Key} {id.Parameter["name"]}"));
        Assert.All(ids, id => Assert.Equal(1, id.Parameter["schema"]!["minimum"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.BadRequest, answered);
    }

    /// <summary>An authentication scheme that never finds a user, so every request the host guards is challenged.</summary>
    private sealed class AnonymousHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Reads the fake host cannot serve, keyed <c>GET /path</c>, with the reason; one listed here that serves fails.</summary>
    private static readonly Dictionary<string, string> UnservedReads = new(StringComparer.Ordinal);

    /// <summary>
    /// Every read serves the body its document declares: each required member present, each member declared, and each
    /// value of a type its schema allows, so null only where the schema allows null. The staged fakes serve every list
    /// one item, so each item schema is checked too. Without this the response schemas' required lists are only read
    /// back from the generator that wrote them.
    /// </summary>
    [Fact]
    public async Task Every_read_serves_the_body_its_document_declares()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
        var schemas = document["components"]!["schemas"]!.AsObject();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: StagedFakes.Create());
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var served = 0;
        var wrong = new List<string>();
        foreach (var (path, item) in Members(document["paths"]))
        {
            if (item?["get"] is not { } operation)
            {
                continue;
            }

            var key = $"GET {path}";
            var response = await client.GetAsync("/acta/api" + ReadUrl(path, operation), ct);
            if (UnservedReads.TryGetValue(key, out var reason))
            {
                if (response.IsSuccessStatusCode)
                {
                    wrong.Add($"{key}: listed as unserved ({reason}) but answered {(int)response.StatusCode}.");
                }
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                wrong.Add($"{key}: answered {(int)response.StatusCode}; serve it from the fakes or list it in UnservedReads.");
                continue;
            }

            served++;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (operation["responses"]?["200"]?["content"]?[contentType] is null)
            {
                wrong.Add($"{key}: served {contentType}, which the document does not declare for 200.");
                continue;
            }
            if (operation["responses"]?["200"]?["content"]?["application/json"]?["schema"] is not { } schema)
            {
                wrong.Add($"{key}: the document declares no JSON body for 200.");
                continue;
            }
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body is JsonObject page && page["items"] is JsonArray { Count: 0 })
            {
                wrong.Add($"{key}: served an empty page; stage an item in StagedFakes.");
            }
            Describe(body, schema, schemas, key, wrong);
        }

        Assert.True(served > 30, $"only {served} reads served; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A payload read answers the payload itself, as the document declares: a JSON payload as application/json with the
    /// stored bytes, any other format and stored JSON that is not strict JSON as application/octet-stream with its format
    /// id in X-Acta-Payload-Format, no payload as 204, and an unknown job as 404. Neither truncates; the inline limit is
    /// the envelope's alone.
    /// </summary>
    [Fact]
    public async Task A_payload_read_answers_the_payload_itself()
    {
        var operations = Operations(await ActaOpenApiDocument.GenerateAsync());
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(
            fakes: fakes,
            configureBuilder: builder => builder.Services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 16)
        );
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var json = Encoding.UTF8.GetBytes("""{"invoiceId": 12345678901234567890, "note": "past the inline limit"}""");
        byte[] binary = [1, 2, 3];
        var malformed = "{broken"u8.ToArray();
        var answers = new List<string>();
        foreach (
            var (route, store) in new (string, Action<JobPayload?>)[]
            {
                ("input", payload => fakes.Jobs.StoredInput = payload),
                ("result", payload => fakes.Jobs.StoredResult = payload),
            }
        )
        {
            var declared = operations[$"GET /v1/jobs/{{jobRef}}/{route}"]["responses"]!;
            Assert.Equal(["application/json", "application/octet-stream"], declared["200"]!["content"]!.AsObject().Select(c => c.Key));
            Assert.NotNull(declared["200"]!["headers"]!["X-Acta-Payload-Format"]);
            Assert.NotNull(declared["204"]);
            Assert.NotNull(declared["404"]);

            store(JobPayload.FromBytes(JobPayloadFormat.Json, json));
            answers.Add(await AnswerAsync($"jobs/{FakeJobs.FoundJobRef}/{route}"));
            store(JobPayload.FromBytes(JobPayloadFormat.Custom(128, "acme-binary"), binary));
            answers.Add(await AnswerAsync($"jobs/{FakeJobs.FoundJobRef}/{route}"));
            store(JobPayload.Trusted(JobPayloadFormat.Json, malformed));
            answers.Add(await AnswerAsync($"jobs/{FakeJobs.FoundJobRef}/{route}"));
            store(null);
            answers.Add(await AnswerAsync($"jobs/{FakeJobs.FoundJobRef}/{route}"));
            answers.Add(await AnswerAsync($"jobs/{FakeJobs.MissingJobRef}/{route}"));
        }

        string[] expected =
        [
            $"200 application/json format=- {Convert.ToHexString(json)}",
            "200 application/octet-stream format=128 010203",
            $"200 application/octet-stream format=1 {Convert.ToHexString(malformed)}",
            "204  format=- ",
            "404 application/problem+json format=-",
        ];
        Assert.Equal([.. expected, .. expected], answers);

        async Task<string> AnswerAsync(string path)
        {
            using var response = await client.GetAsync("/acta/api/v1/" + path, ct);
            var format = response.Headers.TryGetValues("X-Acta-Payload-Format", out var values) ? string.Join(",", values) : "-";
            var answer = $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} format={format}";
            return response.IsSuccessStatusCode
                ? $"{answer} {Convert.ToHexString(await response.Content.ReadAsByteArrayAsync(ct))}"
                : answer;
        }
    }

    // The read's path with each placeholder filled, and each required query parameter set, from OpenApiExamples.RouteValues.
    private static string ReadUrl(string path, JsonNode operation)
    {
        var url = PathPlaceholders(path)
            .Aggregate(
                path,
                (filled, name) =>
                    filled.Replace($"{{{name}}}", Uri.EscapeDataString(OpenApiExamples.RouteValues[name]), StringComparison.Ordinal)
            );
        var query = Elements(operation["parameters"])
            .Where(parameter => parameter?["in"]?.GetValue<string>() == "query" && parameter["required"]?.GetValue<bool>() == true)
            .Select(parameter => parameter!["name"]!.GetValue<string>())
            .Select(name => $"{name}={Uri.EscapeDataString(OpenApiExamples.RouteValues[name])}")
            .ToList();
        return query.Count == 0 ? url : url + "?" + string.Join('&', query);
    }

    // Adds to wrong what a served value breaks of its schema: a type the schema does not allow, an absent required member,
    // or a member it does not declare. A reference is followed, and a oneOf holds when one of its branches does.
    private static void Describe(JsonNode? value, JsonNode schema, JsonObject schemas, string where, List<string> wrong)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            Describe(value, schemas[reference.Split('/')[^1]]!, schemas, where, wrong);
            return;
        }

        // A schema that is a description alone takes any JSON value.
        if (schema.AsObject().All(keyword => keyword.Key is "description"))
        {
            return;
        }

        if (schema["oneOf"] is JsonArray branches)
        {
            if (!branches.Any(branch => Breaks(value, branch!, schemas, where).Count == 0))
            {
                wrong.Add($"{where}: {Kind(value)} matches no branch of its oneOf.");
            }
            return;
        }

        if (schema["type"] is { } type && !TypeAllows(type, Kind(value)))
        {
            wrong.Add($"{where}: {Kind(value)} where the schema allows {type.ToJsonString()}.");
            return;
        }

        // An object schema that declares no members takes any.
        if (value is JsonObject members && (schema["properties"] is not null || schema["additionalProperties"] is not null))
        {
            foreach (
                var required in Elements(schema["required"])
                    .Select(name => name!.GetValue<string>())
                    .Where(name => !members.ContainsKey(name))
            )
            {
                wrong.Add($"{where}: required member '{required}' is absent.");
            }
            foreach (var (name, member) in members)
            {
                if (schema["properties"]?[name] is { } declared)
                {
                    Describe(member, declared, schemas, $"{where}.{name}", wrong);
                }
                else if (schema["additionalProperties"] is JsonObject additional)
                {
                    Describe(member, additional, schemas, $"{where}.{name}", wrong);
                }
                else
                {
                    wrong.Add($"{where}: member '{name}' is not declared.");
                }
            }
        }
        else if (value is JsonArray items && schema["items"] is { } itemSchema)
        {
            for (var i = 0; i < items.Count; i++)
            {
                Describe(items[i], itemSchema, schemas, $"{where}[{i}]", wrong);
            }
        }
    }

    private static List<string> Breaks(JsonNode? value, JsonNode schema, JsonObject schemas, string where)
    {
        var wrong = new List<string>();
        Describe(value, schema, schemas, where, wrong);
        return wrong;
    }

    private static string Kind(JsonNode? value) =>
        value?.GetValueKind() switch
        {
            null or JsonValueKind.Null => "null",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => value.AsValue().TryGetValue<long>(out _) ? "integer" : "number",
        };

    // A schema's type, one name or an array of them, allows a kind; a number allows an integer too.
    private static bool TypeAllows(JsonNode type, string kind) =>
        (type is JsonArray names ? names.Select(name => name!.GetValue<string>()) : [type.GetValue<string>()]).Any(name =>
            name == kind || (name == "number" && kind == "integer")
        );

    private static readonly (string Verb, string Request)[] OutboxVerbs =
    [
        ("discard", "DiscardOutboxRequest"),
        ("requeue", "RequeueOutboxRequest"),
    ];

    /// <summary>
    /// An outbox verb's ids are absent for every quarantined row or name at least one: the schema says so with
    /// <c>minItems</c>, and the operation answers an empty list with 400 and a list of one with something else.
    /// </summary>
    [Fact]
    public async Task Outbox_ids_name_at_least_one_row()
    {
        var schemas = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!;
        var answers = new List<(string Verb, HttpStatusCode Empty, HttpStatusCode One)>();
        await WithSqliteHostAsync(async client =>
        {
            foreach (var (verb, _) in OutboxVerbs)
            {
                var path = $"outbox/{SqliteNamespace}/{verb}";
                answers.Add(
                    (
                        verb,
                        await SendControlAsync(client, HttpMethod.Post, path, """{"outboxIds":[]}"""),
                        await SendControlAsync(client, HttpMethod.Post, path, $$"""{"outboxIds":["{{Guid.NewGuid()}}"]}""")
                    )
                );
            }
        });

        Assert.All(OutboxVerbs, verb => Assert.Equal(1, schemas[verb.Request]!["properties"]!["outboxIds"]!["minItems"]!.GetValue<int>()));
        Assert.Equal(OutboxVerbs.Length, answers.Count);
        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.BadRequest, answer.Empty));
        Assert.All(answers, answer => Assert.NotEqual(HttpStatusCode.BadRequest, answer.One));
    }

    /// <summary>
    /// The introduction's empty-body rule holds: a control whose request object is optional answers 400 when it declares
    /// application/json and sends no bytes, while a signal raise reads the same request as a presence-only signal.
    /// </summary>
    [Fact]
    public async Task An_empty_json_body_answers_as_the_introduction_says()
    {
        var description = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["info"]!["description"]!.GetValue<string>();
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, fakes: fakes);
        await using var _ = app;

        var pause = await SendEmptyJsonAsync(client, $"jobs/{FakeJobs.FoundJobRef}/pause");
        var signal = await SendEmptyJsonAsync(client, $"jobs/{FakeJobs.FoundJobRef}/signals/approval");

        Assert.Contains("one that declares application/json and sends no bytes answers 400", description, StringComparison.Ordinal);
        Assert.Contains("no bytes, with or without the content type, raise a presence-only signal", description, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, pause);
        Assert.Equal(HttpStatusCode.OK, signal);
        Assert.Null(Assert.Single(fakes.Jobs.SignalCalls).Value);
    }

    private static async Task<HttpStatusCode> SendEmptyJsonAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/" + path) { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Acta-Control", "true");
        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }
}
