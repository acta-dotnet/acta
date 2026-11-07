using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// The document's examples, taken from what the host over <see cref="StagedFakes"/> answers: each read's 200 body, and for
/// each control one request from <see cref="Requests"/> with the 200 it answers. Only the requests are written by hand,
/// and a request the server does not accept, or a read it does not serve, fails the generation that sends it.
/// </summary>
internal static class OpenApiExamples
{
    /// <summary>The value each path placeholder and required query parameter takes, so the fake host serves the route.</summary>
    public static readonly IReadOnlyDictionary<string, string> RouteValues = new Dictionary<string, string>(StringComparer.Ordinal)
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
        ["signalName"] = "approval",
        ["tagName"] = "env",
    };

    /// <summary>One request body each control accepts, keyed as <c>METHOD /path</c> below the version segment.</summary>
    public static readonly IReadOnlyDictionary<string, string> Requests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["POST /jobs"] = """
            {"jobNamespace":"billing","jobName":"send-invoice","input":{"json":{"invoiceId":42}},"deduplicationKey":"invoice-42",
            "lane":"reports","concurrencyKey":"customer:42","tags":[{"name":"source","value":"api"}]}
            """,
        ["POST /jobs/{jobRef}/pause"] = """{"reasonMessage":"Waiting for the provider.","expectedVersion":7}""",
        ["POST /jobs/{jobRef}/resume"] = """{"reasonMessage":"The provider is back.","expectedVersion":7}""",
        ["POST /jobs/{jobRef}/restart"] = """{"reasonMessage":"Fixed the card on file.","expectedVersion":7}""",
        ["POST /jobs/{jobRef}/cancel"] = """{"reasonMessage":"The order was refunded.","expectedVersion":7}""",
        ["POST /jobs/{jobRef}/purge"] = """{"reasonMessage":"Duplicate of an earlier import."}""",
        ["POST /jobs/{jobRef}/reschedule"] =
            """{"nextRunAtUtc":"2026-06-13T09:00:00Z","reasonMessage":"Run after the batch.","expectedVersion":7}""",
        ["POST /jobs/{jobRef}/reprioritize"] = """{"priority":"high","reasonMessage":"The customer is waiting.","expectedVersion":7}""",
        ["PUT /jobs/{jobRef}/input"] = """{"input":{"json":{"invoiceId":43}},"reasonMessage":"Corrected the invoice id."}""",
        ["POST /jobs/{jobRef}/signals/{signalName}"] = """{"approved":true}""",
        ["PUT /jobs/{jobRef}/tags/{tagName}"] = """{"value":"prod"}""",
        ["PUT /namespaces/{jobNamespace}/tags/{tagName}"] = """{"value":"prod"}""",
        ["PUT /tenants/{tenantName}/tags/{tagName}"] = """{"value":"prod"}""",
        ["PUT /definitions/{jobNamespace}/{jobName}/tags/{tagName}"] = """{"value":"prod"}""",
        ["POST /namespaces/{jobNamespace}/suspend"] = """{"reasonMessage":"Provider outage."}""",
        ["POST /namespaces/{jobNamespace}/resume"] = """{"reasonMessage":"Provider recovered."}""",
        ["PUT /namespaces/{jobNamespace}"] = """{"ownerTeam":"payments","description":"Invoices and receipts.","expectedVersion":3}""",
        ["POST /tenants"] = """{"tenantName":"cust-002","displayName":"Globex","description":"Globex Corporation"}""",
        ["POST /tenants/{tenantName}/suspend"] = """{"reasonMessage":"Unpaid balance."}""",
        ["POST /tenants/{tenantName}/resume"] = """{"reasonMessage":"Balance settled."}""",
        ["PUT /tenants/{tenantName}"] = """{"displayName":"Acme","description":"Acme Corp","expectedVersion":0}""",
        ["POST /alerts/{alertRef}/acknowledge"] = """{"reasonMessage":"Looking into it."}""",
        ["POST /alerts/{alertRef}/resolve"] = """{"reasonMessage":"The provider is back."}""",
        ["POST /outbox/{jobNamespace}/requeue"] =
            """{"outboxIds":["00000000-0000-0000-0000-00000000000a"],"reasonMessage":"Fixed the payload."}""",
        ["POST /outbox/{jobNamespace}/discard"] =
            """{"outboxIds":["00000000-0000-0000-0000-00000000000a"],"reasonMessage":"Sent by hand."}""",
        ["PUT /definitions/{jobNamespace}/{jobName}/overrides"] =
            """{"overrides":{"maxAttempts":5,"priority":"high"},"expectedVersion":0}""",
        ["POST /definitions/{jobNamespace}/{jobName}/retire"] = """{"expectedVersion":0,"reasonMessage":"Replaced by send-receipt."}""",
        ["POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause"] =
            """{"pausedUntilUtc":"2026-06-13T06:00:00Z","reasonMessage":"Maintenance."}""",
        ["POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/resume"] = """{"reasonMessage":"Maintenance is over."}""",
        ["POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger"] = """{"reasonMessage":"Catch up after the outage."}""",
        ["POST /calendars/{jobNamespace}/{calendarName}/drafts"] =
            """{"role":"availability","definition":{"kind":"weekly-availability"},"expectedLatestVersion":2}""",
        ["POST /calendars/{jobNamespace}/{calendarName}/publish"] = """{"expectedCurrentVersion":1,"candidateVersion":2}""",
        ["POST /calendars/{jobNamespace}/{calendarName}/archive"] =
            """{"expectedCurrentVersion":1,"reasonMessage":"No schedule uses it."}""",
    };

    /// <summary>
    /// Every route the host maps below <paramref name="apiRoot"/>, sent once, reads before controls: the request each
    /// sends and the success it answers, keyed by the operation id the route declares. A 201 is sent again, since the
    /// repeat of an enqueue is its 200.
    /// </summary>
    public static async Task<Dictionary<string, Example>> CaptureAsync(WebApplication app, string apiRoot)
    {
        var root = apiRoot + ActaEndpointRouteBuilderExtensions.ApiVersionSegment;
        var routes = app
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(root + "/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method =>
                    (Key: $"{method} {endpoint.RoutePattern.RawText![root.Length..]}", Endpoint: endpoint)
                )
            )
            .OrderBy(route => !route.Key.StartsWith("GET ", StringComparison.Ordinal))
            .ThenBy(route => route.Key, StringComparer.Ordinal)
            .ToList();

        var client = app.GetTestClient();
        var examples = new Dictionary<string, Example>(StringComparer.Ordinal);
        foreach (var (key, endpoint) in routes)
        {
            var space = key.IndexOf(' ', StringComparison.Ordinal);
            var url = root + Url(key[(space + 1)..], endpoint);
            var body =
                endpoint.Metadata.GetMetadata<RequestBodyDoc>() is null ? null
                : Requests.TryGetValue(key, out var accepted) ? accepted.Trim()
                : throw new InvalidOperationException($"{key} reads a body: add a request it accepts to OpenApiExamples.Requests.");
            var response = await SendAsync(client, key[..space], url, body);
            if (response.Status == HttpStatusCode.Created)
            {
                response = await SendAsync(client, key[..space], url, body);
            }

            if ((int)response.Status is < 200 or > 299)
            {
                throw new InvalidOperationException($"{key} answered {(int)response.Status} to {body}: {response.Text}");
            }
            examples[endpoint.Metadata.GetMetadata<ActaOperationAttribute>()!.OperationId] = new Example(
                body is null ? null : JsonNode.Parse(body),
                ((int)response.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                response.Json ? Stable(JsonNode.Parse(response.Text)) : null
            );
        }
        return examples;
    }

    /// <summary>What one route was sent and answered: its request body, if it reads one, and its success's JSON body.</summary>
    public sealed record Example(JsonNode? Request, string Status, JsonNode? Response);

    // The route's path with each placeholder filled, and each required query parameter set, from RouteValues.
    private static string Url(string path, RouteEndpoint endpoint)
    {
        var filled = path;
        foreach (var (name, value) in RouteValues)
        {
            filled = filled.Replace($"{{{name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        var query = endpoint
            .Metadata.OfType<QueryParameterDoc>()
            .Where(parameter => parameter.Required)
            .Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(RouteValues[parameter.Name])}")
            .ToList();
        return query.Count == 0 ? filled : filled + "?" + string.Join('&', query);
    }

    private static async Task<(HttpStatusCode Status, bool Json, string Text)> SendAsync(
        HttpClient client,
        string method,
        string url,
        string? body
    )
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Acta-Control", "true");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType == "application/json",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    // The engine version the host reports is this build's, which every commit changes; the example states a release's.
    private static JsonNode? Stable(JsonNode? body)
    {
        if (body is JsonObject { } members && members["engineVersion"]?.GetValue<string>() == BuildVersion)
        {
            members["engineVersion"] = "1.0.0";
        }
        return body;
    }

    private static readonly string BuildVersion =
        typeof(IJobs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
