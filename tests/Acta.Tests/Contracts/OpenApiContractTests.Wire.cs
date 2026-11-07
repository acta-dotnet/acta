using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.Tests.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed partial class OpenApiContractTests
{
    /// <summary>The value each path placeholder and required query parameter takes, so the fake host serves the read.</summary>
    private static readonly Dictionary<string, string> FakeRouteValues = new(StringComparer.Ordinal)
    {
        ["jobRef"] = FakeJobs.FoundJobRef.ToString(),
        ["jobNamespace"] = "billing",
        ["jobName"] = "send-invoice",
        ["tenantName"] = "cust-001",
        ["workerRef"] = FakeWorkers.KnownWorkerRef.ToString(),
        ["alertRef"] = FakeAlerts.KnownAlertRef.ToString(),
        ["scheduleName"] = "nightly",
        ["calendarName"] = "office-hours",
        ["deduplicationKey"] = "ck-1",
        ["version"] = "1",
    };

    /// <summary>Reads the fake host cannot serve, keyed <c>GET /path</c>, with the reason; one listed here that serves fails.</summary>
    private static readonly Dictionary<string, string> UnservedReads = new(StringComparer.Ordinal);

    /// <summary>
    /// Every read serves the body its document declares: each required member present, each member declared, and each
    /// value of a type its schema allows, so null only where the schema allows null. Without this the response schemas'
    /// required lists are only read back from the generator that wrote them.
    /// </summary>
    [Fact]
    public async Task Every_read_serves_the_body_its_document_declares()
    {
        var document = JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!;
        var schemas = document["components"]!["schemas"]!.AsObject();
        var fakes = new FakeOperations();
        fakes.Jobs.StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("""{"invoiceId":1}"""));
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
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
            if (operation["responses"]?["200"]?["content"]?["application/json"]?["schema"] is not { } schema)
            {
                wrong.Add($"{key}: the document declares no JSON body for 200.");
                continue;
            }
            Describe(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)), schema, schemas, key, wrong);
        }

        Assert.True(served > 30, $"only {served} reads served; the walk is broken.");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The read's path with each placeholder filled, and each required query parameter set, from FakeRouteValues.
    private static string ReadUrl(string path, JsonNode operation)
    {
        var url = PathPlaceholders(path)
            .Aggregate(
                path,
                (filled, name) => filled.Replace($"{{{name}}}", Uri.EscapeDataString(FakeRouteValues[name]), StringComparison.Ordinal)
            );
        var query = Elements(operation["parameters"])
            .Where(parameter => parameter?["in"]?.GetValue<string>() == "query" && parameter["required"]?.GetValue<bool>() == true)
            .Select(parameter => parameter!["name"]!.GetValue<string>())
            .Select(name => $"{name}={Uri.EscapeDataString(FakeRouteValues[name])}")
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

        if (value is JsonObject members)
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
