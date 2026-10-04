using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>
/// A route segment that does not parse: caller input, so the 400 every ref-addressed route gives, whose message is
/// the problem's detail. No parameter name, which would append "(Parameter '...')" to that detail.
/// </summary>
internal sealed class MalformedRouteKeyException(string segment, string entity)
    : ArgumentException($"{segment} is not a valid {entity} ref.");

/// <summary>The route keys a group's table addresses its targets by, read from the matched route values.</summary>
internal static class RouteKeys
{
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
