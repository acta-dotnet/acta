using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.Tests.Contracts;

// The document's error examples, each the body the host answered to a request that provoked the error, so every one is a
// problem document or a control's response the server writes. Only the provoking requests are written here: each status
// has its ways to reach it, tried in order, and a route that declares a status none of them reaches fails the generation.
internal static partial class OpenApiExamples
{
    /// <summary>
    /// The mount the errors are provoked on: a documented mount's routes carry the body declaration the generator reads,
    /// which in a serving host answers a wrong content type before the route does.
    /// </summary>
    public const string ErrorsMount = "/probe-errors";

    /// <summary>The mount whose every route checks a permission the caller does not hold, so each answers its 403.</summary>
    public const string DeniedMount = "/probe-denied";

    /// <summary>The mount that takes id: and a job's number wherever a job route takes the debug lookup.</summary>
    public const string NumericMount = "/probe-numeric";

    /// <summary>The value no route key can name: a space is in no name, key, or ref.</summary>
    private const string Malformed = "bad key";

    /// <summary>The members a stale-version conflict sets to a version the staged fakes never hold.</summary>
    private static readonly string[] VersionMembers = ["expectedVersion", "expectedLatestVersion", "expectedCurrentVersion"];

    /// <summary>The query filters that name a job or a worker, which answer 404 for one that does not exist.</summary>
    private static readonly string[] RefFilters = ["jobRef", "parentJobRef", "lineageRootJobRef", "workerRef"];

    /// <summary>
    /// For every route below <paramref name="apiRoot"/> and each error status it documents with a body, what the host
    /// answered the first request that provoked it, keyed by the route's operation id and the status.
    /// </summary>
    public static async Task<Dictionary<(string Operation, string Status), JsonNode>> CaptureErrorsAsync(
        WebApplication app,
        string apiRoot,
        FacadeFaults faults
    )
    {
        var root = apiRoot + ActaEndpointRouteBuilderExtensions.ApiVersionSegment;
        var errorsRoot = ErrorsMount + "/api" + ActaEndpointRouteBuilderExtensions.ApiVersionSegment;
        var cap = app.Services.GetRequiredService<IOptions<JobsOptions>>().Value.MaxInlinePayloadBytes;
        var client = app.GetTestClient();
        var errors = new Dictionary<(string Operation, string Status), JsonNode>();
        var unprovoked = new List<string>();
        foreach (var (key, endpoint) in Routes(app, root))
        {
            var space = key.IndexOf(' ', StringComparison.Ordinal);
            var (method, path) = (key[..space], key[(space + 1)..]);
            var operation = endpoint.Metadata.GetMetadata<ActaOperationAttribute>()!.OperationId;
            foreach (
                var declared in endpoint
                    .Metadata.OfType<IProducesResponseTypeMetadata>()
                    .Where(produces => produces.StatusCode >= 400 && produces.Type is { } type && type != typeof(void))
                    .GroupBy(produces => produces.StatusCode)
            )
            {
                var mediaTypes = declared.SelectMany(produces => produces.ContentTypes).ToHashSet(StringComparer.Ordinal);
                var answers = new List<string>();
                JsonNode? provoked = null;
                foreach (var attempt in Attempts(declared.Key, endpoint, cap))
                {
                    var url = (attempt.Root ?? errorsRoot) + Url(path, endpoint, attempt.Values, attempt.OmitRequiredQuery, attempt.Query);
                    var body = attempt.Body(Requests.TryGetValue(key, out var accepted) ? accepted.Trim() : null);
                    faults.Fault = attempt.Fault;
                    (HttpStatusCode Status, string MediaType, string Text) answer;
                    try
                    {
                        answer = await SendAsync(client, method, url, body, attempt.ContentType, attempt.Confirm);
                    }
                    finally
                    {
                        faults.Fault = FacadeFault.None;
                    }

                    if ((int)answer.Status == declared.Key && mediaTypes.Contains(answer.MediaType))
                    {
                        provoked = JsonNode.Parse(answer.Text);
                        break;
                    }
                    answers.Add($"{attempt.Label}: {(int)answer.Status} {answer.MediaType}");
                }

                if (provoked is null)
                {
                    unprovoked.Add($"{key} declares {declared.Key}, and no request provokes it: {string.Join("; ", answers)}.");
                    continue;
                }
                errors[(operation, declared.Key.ToString(CultureInfo.InvariantCulture))] = provoked;
            }
        }
        return unprovoked.Count == 0 ? errors : throw new InvalidOperationException(string.Join("\n", unprovoked));
    }

    /// <summary>
    /// The operation ids of the job routes that take the debug lookup: each sent its example request on a host with
    /// numeric lookup on, with id: and the staged job's number for its job ref, and answering anything but a 400.
    /// </summary>
    public static async Task<HashSet<string>> CaptureNumericJobRoutesAsync(WebApplication app, string apiRoot)
    {
        var root = apiRoot + ActaEndpointRouteBuilderExtensions.ApiVersionSegment;
        var numericRoot = NumericMount + "/api" + ActaEndpointRouteBuilderExtensions.ApiVersionSegment;
        var client = app.GetTestClient();
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, endpoint) in Routes(app, root).Where(route => route.Key.Contains("{jobRef}", StringComparison.Ordinal)))
        {
            var space = key.IndexOf(' ', StringComparison.Ordinal);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["jobRef"] = "id:42" };
            var url = numericRoot + Url(key[(space + 1)..], endpoint, values);
            var body = Requests.TryGetValue(key, out var accepted) ? accepted.Trim() : null;
            if ((await SendAsync(client, key[..space], url, body)).Status != HttpStatusCode.BadRequest)
            {
                routes.Add(endpoint.Metadata.GetMetadata<ActaOperationAttribute>()!.OperationId);
            }
        }
        return routes;
    }

    /// <summary>
    /// One request to try for an error: route keys and query values set over the example's, the example's body changed
    /// or replaced, on another mount, without the confirmation, or with the facades failing.
    /// </summary>
    private sealed record Attempt(string Label)
    {
        public IReadOnlyDictionary<string, string>? Values { get; init; }

        public IReadOnlyDictionary<string, string>? Query { get; init; }

        public bool OmitRequiredQuery { get; init; }

        public Func<string?, string?> Body { get; init; } = body => body;

        public string ContentType { get; init; } = "application/json";

        public bool Confirm { get; init; } = true;

        public string? Root { get; init; }

        public FacadeFault Fault { get; init; }
    }

    // The ways to each status, in the order tried. A malformed route key, an empty body, a malformed or missing query value,
    // then a call without its confirmation are the 400; a target the staged fakes know as missing, by route key or filter,
    // the 404; one they know as rejected, a stale expected version, or a rejected namespace in the body the 409.
    private static IEnumerable<Attempt> Attempts(int status, RouteEndpoint endpoint, int cap)
    {
        var keys = endpoint.RoutePattern.Parameters.Select(parameter => parameter.Name).ToList();
        var body = endpoint.Metadata.GetMetadata<RequestBodyDoc>();
        var query = endpoint.Metadata.OfType<QueryParameterDoc>().ToList();
        switch (status)
        {
            case 400:
                foreach (var key in keys)
                {
                    yield return new($"{key} {Malformed}") { Values = One(key, Malformed) };
                }
                if (body is not null)
                {
                    yield return new("an empty object") { Body = _ => "{}" };
                }
                foreach (var parameter in query.OrderBy(QueryRank))
                {
                    yield return new($"{parameter.Name} {Malformed}") { Query = One(parameter.Name, Malformed) };
                }
                if (query.Any(parameter => parameter.Required))
                {
                    yield return new("no required query") { OmitRequiredQuery = true };
                }
                yield return new("unconfirmed") { Confirm = false, Body = _ => null };
                break;
            case 403:
                yield return new("no permission") { Root = DeniedMount + "/api" + ActaEndpointRouteBuilderExtensions.ApiVersionSegment };
                break;
            case 404:
                foreach (var key in keys)
                {
                    if (Missing(key) is { } missing)
                    {
                        yield return new($"{key} missing") { Values = One(key, missing) };
                    }
                }
                foreach (var parameter in query.Where(parameter => RefFilters.Contains(parameter.Name)))
                {
                    yield return new($"{parameter.Name} missing") { Query = One(parameter.Name, Missing(parameter.Name)!) };
                }
                foreach (var parameter in query.Where(parameter => parameter.Required))
                {
                    yield return new($"{parameter.Name} missing") { Values = One(parameter.Name, "missing") };
                }
                break;
            case 409:
                foreach (var key in keys)
                {
                    if (Rejected(key) is { } rejected)
                    {
                        yield return new($"{key} rejected") { Values = One(key, rejected) };
                    }
                }
                if (body is not null)
                {
                    var members = Members(body.BodyType);
                    if (VersionMembers.Any(members.Contains))
                    {
                        yield return new("a stale version")
                        {
                            Body = example => With(example, VersionMembers.Where(members.Contains), 999),
                        };
                    }
                    if (members.Contains("jobNamespace"))
                    {
                        yield return new("a rejected namespace") { Body = example => With(example, ["jobNamespace"], "rejected") };
                    }
                }
                break;
            case 422:
                foreach (var key in keys.Where(key => key is "scheduleName" or "calendarName"))
                {
                    yield return new($"{key} unevaluable") { Values = One(key, "unevaluable") };
                }
                break;
            case 413:
                yield return new("an oversized body") { Body = _ => $$"""{"reasonMessage":"{{new string('a', cap)}}"}""" };
                break;
            case 415:
                yield return new("a text body") { Body = _ => "x", ContentType = "text/plain" };
                break;
            case 500:
                yield return new("a failing server") { Fault = FacadeFault.Server };
                break;
            case 503:
                yield return new("an unreachable database") { Fault = FacadeFault.Database };
                break;
        }
    }

    private static Dictionary<string, string> One(string key, string value) => new(StringComparer.Ordinal) { [key] = value };

    // A query value of no type, or a name no filter can match; a number's message is the plainest, so it goes first.
    private static int QueryRank(QueryParameterDoc parameter) =>
        parameter switch
        {
            { Kind: QueryParameterKind.Int } => 0,
            { Kind: QueryParameterKind.Long } => 1,
            { Kind: QueryParameterKind.Bool } => 2,
            { Kind: QueryParameterKind.Instant } => 3,
            { CodeKind: not null } => 4,
            _ when RefFilters.Contains(parameter.Name) => 5,
            _ => 6,
        };

    // The value of each route key the staged fakes know as absent, or as refusing the control.
    private static string? Missing(string key) =>
        key switch
        {
            "jobRef" or "parentJobRef" or "lineageRootJobRef" => FakeJobs.MissingJobRef.ToString(),
            "alertRef" => new AlertRef(Guid.Empty).ToString(),
            "workerRef" => new WorkerRef(Guid.Empty).ToString(),
            "version" => null,
            _ => "missing",
        };

    private static string? Rejected(string key) =>
        key switch
        {
            "jobRef" => FakeJobs.RejectedJobRef.ToString(),
            "alertRef" or "workerRef" or "version" => null,
            _ => "rejected",
        };

    private static HashSet<string> Members(Type body) =>
        ActaApiJsonContext.Default.GetTypeInfo(body) is { } info
            ? info.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            : [];

    // The example body with each member set to the value; a body that is no object, or a route without one, is left be.
    private static string? With(string? example, IEnumerable<string> members, JsonNode value)
    {
        if (JsonNode.Parse(example ?? "{}") is not JsonObject body)
        {
            return example;
        }
        foreach (var member in members)
        {
            body[member] = value.DeepClone();
        }
        return body.ToJsonString();
    }
}
