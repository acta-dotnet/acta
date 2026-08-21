using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Schedules;

/// <summary>
/// POST schedule-control endpoints: thin HTTP wrappers over the <see cref="ISchedules"/> verbs. A
/// schedule is addressed by its natural key in the route
/// (<c>/schedules/{jobNamespace}/{jobName}/{scheduleName}/…</c>), the same triple the tag routes
/// already use, so the target is visible in the path like every other control family. The verbs own
/// transition legality and audit stamping; this layer validates the request shape and maps
/// <see cref="ControlAction"/> to 200 (applied), 409 (rejected), and 404 (not found).
/// </summary>
internal static class ScheduleControlEndpoints
{
    public static void Map(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // The four schedule verbs share one response shape across all three outcomes, so they declare
        // it once here rather than four times below. Applied, rejected, and not-found carry the same
        // body, so a client reads `action` without special-casing the status code.
        var group = outer.MapGroup("");
        group.ProducesJson<ScheduleControlResponse>();
        group.ProducesJson<ScheduleControlResponse>(StatusCodes.Status409Conflict);
        group.ProducesJson<ScheduleControlResponse>(StatusCodes.Status404NotFound);

        group
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/pause",
                async Task<IResult> (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                {
                    // The body is optional: a bare POST pauses indefinitely with no reason.
                    var (body, error) = await ControlEndpointValidation.ReadOptionalJsonBodyAsync(
                        http,
                        options,
                        DashboardJsonContext.Default.SchedulePauseRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    var reason = body?.ReasonMessage?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
                    if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                    {
                        return reasonError;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    return await Invoke(
                        "pause",
                        async () =>
                            await operations.Schedules.PauseAsync(
                                Lookup(jobNamespace, jobName, scheduleName),
                                body?.PausedUntilUtc,
                                reason,
                                actorKey,
                                ct
                            )
                    );
                }
            )
            // The body is read manually rather than bound, so the document only learns its shape from
            // this declaration; optional because a bare POST pauses indefinitely with no reason.
            .AcceptsJson<SchedulePauseRequest>(optional: true)
            .WithSummary("Pause a recurring schedule, indefinitely or until an instant.");

        group
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/resume",
                async Task<IResult> (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    return await Invoke(
                        "resume",
                        async () =>
                            await operations.Schedules.ResumeAsync(Lookup(jobNamespace, jobName, scheduleName), reason, actorKey, ct)
                    );
                }
            )
            .AcceptsJson<JobControlRequest>(optional: true)
            .WithSummary("Resume a paused schedule, reconciled by its misfire policy.");

        group
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger",
                async Task<IResult> (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    return await Invoke(
                        "trigger",
                        async () =>
                            await operations.Schedules.TriggerNowAsync(Lookup(jobNamespace, jobName, scheduleName), reason, actorKey, ct)
                    );
                }
            )
            .AcceptsJson<JobControlRequest>(optional: true)
            .WithSummary("Fire the schedule now, without moving its cadence.");

        group
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides",
                async Task<IResult> (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    // Mandatory body: expectedVersion is the CAS token and cannot default.
                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.SetScheduleOverridesRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (body!.ExpectedVersion is not { } version)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "expectedVersion is required."
                        );
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    return await Invoke(
                        "overrides",
                        async () =>
                            await operations.Schedules.UpdateOverridesAsync(
                                Lookup(jobNamespace, jobName, scheduleName),
                                version,
                                body.Expression,
                                body.TimeZoneId,
                                body.ReasonMessage,
                                actorKey,
                                ct
                            )
                    );
                }
            )
            .AcceptsJson<SetScheduleOverridesRequest>()
            .WithSummary("Set or clear the schedule's expression and time-zone overrides.");
    }

    private static ScheduleLookup Lookup(string jobNamespace, string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName);

    // A malformed identifier or an invalid override value surfaces from the facade as
    // ArgumentException: caller input, so 400 rather than the sanitized 500 backstop.
    private static async Task<IResult> Invoke(string verb, Func<ValueTask<ScheduleControlResult>> action)
    {
        try
        {
            return ToResult(verb, await action());
        }
        catch (ArgumentException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid schedule request.", ex.Message);
        }
    }

    private static IResult ToResult(string verb, ScheduleControlResult result)
    {
        var (statusCode, message) = result.Action switch
        {
            ControlAction.Applied => (StatusCodes.Status200OK, $"Schedule {verb} applied."),
            ControlAction.Rejected => (
                StatusCodes.Status409Conflict,
                $"Schedule {verb} rejected: the schedule's current state does not allow it."
            ),
            _ => (StatusCodes.Status404NotFound, "Schedule not found."),
        };

        return Results.Json(
            new ScheduleControlResponse(result.Action, result.Status, result.PausedUntilUtc, result.NextRunAtUtc, result.Version, message),
            DashboardJsonContext.Default.ScheduleControlResponse,
            statusCode: statusCode
        );
    }
}
