using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The tag routes of the targets whose group has not moved to a route table yet, over <see cref="TagRoutes"/>.
/// Patterns and route parameters are spelled out per endpoint so the Request Delegate Generator can bind them.
/// </summary>
internal static class TagEndpoints
{
    public static void MapReads(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // A nested empty-prefix group so the reads declare their one shared contract once. They all
        // funnel through TagRoutes.ReadAsync, so they all answer the same ways: the tag list, the 404
        // for an addressable target that carries none, and the group-wide 400 for a target that is not
        // addressable at all.
        var group = outer.MapGroup("");
        group.ProducesJson<IReadOnlyList<TagItem>>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}/tags",
                (string jobRef, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.ReadAsync(operations, () => JobTarget(jobRef, options), ct)
            )
            .WithSummary("Read the job's tags.");
        group
            .MapGet(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
                (string jobNamespace, string jobName, string scheduleName, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.ReadAsync(operations, () => ScheduleTarget(jobNamespace, jobName, scheduleName), ct)
            )
            .WithSummary("Read the schedule's tags.");
    }

    public static void MapControls(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // Same shape for every mutation: each one resolves a target then applies, so an
        // applied change and a target the write matched no row for both answer with the same
        // AdminControlResult and a client reads `action` without special-casing the status code. A
        // target that is not addressable never gets that far; it is the group-wide 400.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResult>();
        group.ProducesJson<AdminControlResult>(StatusCodes.Status404NotFound);

        // The upserts read a JSON body and can refuse a content type that is not JSON; the
        // removes name their tag in the route and read nothing, so they cannot. That is the one
        // line the two halves do not share, which is the whole reason for the nested group.
        var withBody = group.MapGroup("");
        withBody.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        withBody
            .MapPost(
                "/jobs/{jobRef}/tags",
                (string jobRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.UpsertAsync(http, operations, options, () => JobTarget(jobRef, options), ct)
            )
            // The tag body is read manually rather than bound, so the document only learns its shape
            // from these declarations; one per upsert, the same request record every time.
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the job.");
        group
            .MapDelete(
                "/jobs/{jobRef}/tags/{tagName}",
                (string jobRef, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.RemoveAsync(http, operations, options, () => JobTarget(jobRef, options), tagName, ct)
            )
            .WithSummary("Remove one tag from the job.");

        withBody
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
                (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) => TagRoutes.UpsertAsync(http, operations, options, () => ScheduleTarget(jobNamespace, jobName, scheduleName), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the schedule.");
        group
            .MapDelete(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags/{tagName}",
                (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    string tagName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                    TagRoutes.RemoveAsync(http, operations, options, () => ScheduleTarget(jobNamespace, jobName, scheduleName), tagName, ct)
            )
            .WithSummary("Remove one tag from the schedule.");
    }

    /// <summary>
    /// The ref-addressed targets. A ref that does not parse names nothing, and it is caller
    /// input, so it reports the same way a malformed catalog identifier already did and the helpers
    /// above answer both with the 400. No parameter name on the throw: the message is the wire's
    /// detail here, and ArgumentException would append "(Parameter '...')" to it, which would put two
    /// spellings of one refusal on the API.
    /// </summary>
    private static TagTarget JobTarget(string jobRef, ActaEndpointOptions options) =>
        JobTargetBinding.TryParseTarget(jobRef, options, out var lookup)
            ? TagTarget.ForJob(lookup)
            : throw new ArgumentException("jobRef is not a valid job ref.");

    private static TagTarget ScheduleTarget(string jobNamespace, string jobName, string scheduleName) =>
        TagTarget.ForSchedule(new ScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName));
}
