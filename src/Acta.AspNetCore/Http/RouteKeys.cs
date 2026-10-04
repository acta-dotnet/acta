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
}
