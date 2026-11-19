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
        ["kind"] = "The checkpoint's kind as the checkpoint list names it: variable, signal, timer, progress, or child.",
        ["name"] = "The checkpoint's name exactly as the checkpoint list shows it, sys names included; it is not folded to lowercase.",
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

    /// <summary>
    /// A checkpoint kind by the kebab code the checkpoint list prints: variable, signal, timer, progress, or child. Any
    /// other code, the scheduler's firing record and unspecified included, is malformed input, since no value is served
    /// under it.
    /// </summary>
    public static CheckpointKindCode CheckpointKind(string code)
    {
        try
        {
            if (
                CheckpointKindCodeExtensions.FromCode(code)
                is var kind
                    and (
                        CheckpointKindCode.Variable
                        or CheckpointKindCode.Signal
                        or CheckpointKindCode.Timer
                        or CheckpointKindCode.Progress
                        or CheckpointKindCode.Child
                    )
            )
            {
                return kind;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // An unknown code falls through to the same refusal as a known one no value is served under.
        }

        throw new MalformedRouteKeyException("kind is not a valid checkpoint kind.");
    }

    /// <summary>
    /// A checkpoint's name as it is stored, read as given: 1 to 128 lowercase letters, digits, dots, and hyphens, which
    /// holds a child's sys.child name as well as a handler's names. Any other name is malformed input.
    /// </summary>
    public static string CheckpointName(string name) =>
        name.Length is > 0 and <= NameSyntax.ExtendedMaxLength
        && name.All(c => c is '-' or '.' or (>= '0' and <= '9') or (>= 'a' and <= 'z'))
            ? name
            : throw new MalformedRouteKeyException("name is not a valid checkpoint name.");

    /// <summary>A job by its ref alone: a control verb never takes the <c>id:&lt;n&gt;</c> debug lookup.</summary>
    public static JobLookup JobByRef(HttpContext http) =>
        JobRef.TryParse(Value(http, "jobRef"), out var job) ? JobLookup.ByRef(job) : throw new MalformedRouteKeyException("jobRef", "job");
}
