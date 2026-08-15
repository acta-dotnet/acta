using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Schedules;

/// <summary>
/// POST schedule-control endpoints: thin HTTP wrappers over the <see cref="ISchedules"/> verbs. A
/// schedule is addressed by natural key in the JSON body (namespace, job name, schedule name) rather
/// than a route id. The verbs own transition legality and audit stamping; this layer validates the
/// request shape and maps <see cref="ControlAction"/> to 200 (applied), 409 (rejected), and 404
/// (not found).
/// </summary>
internal static class ScheduleControlEndpoints
{
    public static void Map(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // The four schedule verbs share one response shape and one not-found case, so they declare it
        // once here rather than four times below.
        var group = outer.MapGroup("");
        group.ProducesJson<ScheduleControlResponse>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapPost(
                "/schedules/pause",
                async Task<IResult> (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.SchedulePauseRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (!TryLookup(body!.JobNamespace, body.JobName, body.ScheduleName, out var lookup, out var badRequest))
                    {
                        return badRequest;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await operations.Schedules.PauseAsync(lookup, body.PausedUntilUtc, body.ReasonMessage, actorKey, ct);
                    return ToResult("pause", result);
                }
            )
            .WithSummary("Pause a recurring schedule, indefinitely or until an instant.");

        group
            .MapPost(
                "/schedules/resume",
                async Task<IResult> (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.ScheduleResumeRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (!TryLookup(body!.JobNamespace, body.JobName, body.ScheduleName, out var lookup, out var badRequest))
                    {
                        return badRequest;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await operations.Schedules.ResumeAsync(lookup, body.ReasonMessage, actorKey, ct);
                    return ToResult("resume", result);
                }
            )
            .WithSummary("Resume a paused schedule, reconciled by its misfire policy.");

        group
            .MapPost(
                "/schedules/trigger",
                async Task<IResult> (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.ScheduleTriggerRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (!TryLookup(body!.JobNamespace, body.JobName, body.ScheduleName, out var lookup, out var badRequest))
                    {
                        return badRequest;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await operations.Schedules.TriggerNowAsync(lookup, body.ReasonMessage, actorKey, ct);
                    return ToResult("trigger", result);
                }
            )
            .WithSummary("Fire the schedule now, without moving its cadence.");

        group
            .MapPost(
                "/schedules/overrides",
                async Task<IResult> (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.SetScheduleOverridesRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (!TryLookup(body!.JobNamespace, body.JobName, body.ScheduleName, out var lookup, out var badRequest))
                    {
                        return badRequest;
                    }

                    if (body.ExpectedVersion is not { } version)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "version is required."
                        );
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    try
                    {
                        var result = await operations.Schedules.UpdateOverridesAsync(
                            lookup,
                            version,
                            body.Expression,
                            body.TimeZoneId,
                            body.ReasonMessage,
                            actorKey,
                            ct
                        );
                        return ToResult("overrides", result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid schedule overrides.",
                            ex.Message
                        );
                    }
                }
            )
            .WithSummary("Set or clear the schedule's expression and time-zone overrides.");
    }

    private static bool TryLookup(
        string? jobNamespace,
        string? jobName,
        string? scheduleName,
        out ScheduleLookup lookup,
        out IResult badRequest
    )
    {
        if (string.IsNullOrWhiteSpace(jobNamespace) || string.IsNullOrWhiteSpace(jobName) || string.IsNullOrWhiteSpace(scheduleName))
        {
            lookup = default!;
            badRequest = ControlEndpointValidation.Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request.",
                "jobNamespace, jobName, and scheduleName are required."
            );
            return false;
        }

        lookup = new ScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName);
        badRequest = null!;
        return true;
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
