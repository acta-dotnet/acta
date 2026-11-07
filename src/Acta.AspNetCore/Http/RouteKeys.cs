using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>The route keys a group's table addresses its targets by, read from the matched route values.</summary>
internal static class RouteKeys
{
    /// <summary>
    /// What each key names, one sentence apiece: the (test-only) OpenAPI generation writes it on every route whose pattern
    /// has the key, as QueryParameterDoc carries a query parameter's.
    /// </summary>
    public static readonly FrozenDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["jobRef"] = "The job's ref, job_ followed by 32 hex digits.",
        ["alertRef"] = "The alert's ref, alr_ followed by 32 hex digits.",
        ["workerRef"] = "The worker's ref, wrk_ followed by 32 hex digits.",
        ["jobNamespace"] =
            "The namespace's name: the namespace itself, or the one that holds the route's definition, schedule, calendar, or outbox.",
        ["jobName"] = "The job's name, which with jobNamespace names a definition.",
        ["scheduleName"] = "The schedule's name within the definition that jobNamespace and jobName name.",
        ["calendarName"] = "The calendar family's name within jobNamespace; the sys namespace holds the shared families.",
        ["tenantName"] = "The tenant's name.",
        ["tagName"] = "The tag's name, trimmed and lowercased as every tag name is.",
        ["signalName"] = "The signal's name, trimmed and lowercased; sys and the names under it are reserved.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Value(HttpContext http, string name) => (string)http.Request.RouteValues[name]!;

    public static WorkerRef Worker(HttpContext http) =>
        WorkerRef.TryParse(Value(http, "workerRef"), out var parsed) ? parsed : throw new MalformedRouteKeyException("workerRef", "worker");

    public static AlertRef Alert(HttpContext http) =>
        AlertRef.TryParse(Value(http, "alertRef"), out var parsed) ? parsed : throw new MalformedRouteKeyException("alertRef", "alert");

    /// <summary>A job by its ref, or by <c>id:&lt;n&gt;</c> when the host enabled numeric lookup.</summary>
    public static JobLookup Job(HttpContext http, ActaEndpointOptions options) =>
        JobTargetBinding.TryParseTarget(Value(http, "jobRef"), options, out var job)
            ? job
            : throw new MalformedRouteKeyException("jobRef", "job");

    /// <summary>A job by its ref alone: a control verb never takes the <c>id:&lt;n&gt;</c> debug lookup.</summary>
    public static JobLookup JobByRef(HttpContext http) =>
        JobRef.TryParse(Value(http, "jobRef"), out var job) ? JobLookup.ByRef(job) : throw new MalformedRouteKeyException("jobRef", "job");
}
