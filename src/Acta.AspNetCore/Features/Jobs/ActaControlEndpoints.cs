using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// POST job-control endpoints: thin HTTP wrappers over the <see cref="IJobs"/> control verbs. The
/// verbs own transition legality and audit stamping; this layer validates the request shape and
/// maps <see cref="ControlAction"/> to 200 (applied), 409 (rejected), and 404 (not found).
/// </summary>
internal static class ActaControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        MapVerb(
            group,
            options,
            "pause",
            "Pause the job until an operator resumes it.",
            static (jobs, lookup, reason, actorKey, ct) => jobs.PauseAsync(lookup, reason, actorKey, ct)
        );
        MapVerb(
            group,
            options,
            "resume",
            "Resume a paused job.",
            static (jobs, lookup, reason, actorKey, ct) => jobs.ResumeAsync(lookup, reason, actorKey, ct)
        );
        MapVerb(
            group,
            options,
            "restart",
            "Restart a terminal job with its original input.",
            static (jobs, lookup, reason, actorKey, ct) => jobs.RestartAsync(lookup, reason, actorKey, ct)
        );
        MapVerb(
            group,
            options,
            "cancel",
            "Cancel the job.",
            static (jobs, lookup, reason, actorKey, ct) => jobs.CancelAsync(lookup, reason, actorKey, ct)
        );
        // Purge carries no caller reason: the body still passes through MapVerb's confirmation-header and
        // JSON-shape checks (for parity with the other verbs), but the parsed reason is never forwarded.
        MapVerb(
            group,
            options,
            "purge",
            "Delete the job's record from the ledger.",
            static (jobs, lookup, _, actorKey, ct) => jobs.PurgeAsync(lookup, actorKey, ct)
        );
        MapReschedule(group, options);
        MapReprioritize(group, options);
        MapInput(group, options);
        MapSignal(group, options);
    }

    // POST /jobs/{jobRef}/input: amend a job's stored input, round-tripping the job's own payload format.
    // The body carries exactly one of "input" (raw JSON, stored as json), "text" (stored as text), or
    // "base64" (stored under the job's current binary format id). The chosen field must match the stored
    // format, except that "input" is a json fallback for any non-none format (the runner decodes by the
    // stored id). A no-input job has nothing to amend (409); an over-size payload surfaces as 413; an
    // in-flight job is rejected (409) by the verb.
    private static void MapInput(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/jobs/{jobRef}/input",
                async Task<IResult> (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.JobInputRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    var hasInput =
                        body!.Input.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null);
                    if ((hasInput ? 1 : 0) + (body.Text is not null ? 1 : 0) + (body.Base64 is not null ? 1 : 0) != 1)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "Exactly one of input, text, or base64 is required."
                        );
                    }

                    var reason = string.IsNullOrWhiteSpace(body.ReasonMessage) ? null : body.ReasonMessage.Trim();
                    if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                    {
                        return reasonError;
                    }

                    // Resolve the job's current stored format before building the payload so every format
                    // round-trips as itself; a job with no stored input (none) has nothing to amend.
                    var jobId = await jobs.GetJobIdAsync(JobLookup.ByRef(parsed), ct);
                    if (jobId is null)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    var current = await jobs.GetInputAsync(JobLookup.ById(jobId.Value), ct);
                    if (current is not { } stored)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status409Conflict,
                            "No input to amend.",
                            "The job has no input to amend."
                        );
                    }

                    var storedFormat = stored.Format;
                    JobPayload payload;
                    if (body.Text is not null)
                    {
                        if (storedFormat.Id != JobPayloadFormat.Text.Id)
                        {
                            return ControlEndpointValidation.Problem(
                                StatusCodes.Status400BadRequest,
                                "Format mismatch.",
                                $"text can only amend a text-format job; this job's input format is {storedFormat.Name}."
                            );
                        }

                        payload = JobPayload.FromBytes(JobPayloadFormat.Text, System.Text.Encoding.UTF8.GetBytes(body.Text));
                    }
                    else if (body.Base64 is not null)
                    {
                        if (storedFormat.Id != JobPayloadFormat.Bytes.Id && storedFormat.Id < 128)
                        {
                            return ControlEndpointValidation.Problem(
                                StatusCodes.Status400BadRequest,
                                "Format mismatch.",
                                $"base64 can only amend a binary-format job; this job's input format is {storedFormat.Name}."
                            );
                        }

                        byte[] decoded;
                        try
                        {
                            decoded = Convert.FromBase64String(body.Base64);
                        }
                        catch (FormatException)
                        {
                            return ControlEndpointValidation.Problem(
                                StatusCodes.Status400BadRequest,
                                "Invalid request.",
                                "base64 is not valid base64."
                            );
                        }

                        payload = JobPayload.FromBytes(storedFormat, decoded);
                    }
                    else
                    {
                        // Json fallback: accepted for any non-none stored format; the runner decodes by the stored id.
                        payload = JobPayload.FromBytes(JobPayloadFormat.Json, System.Text.Encoding.UTF8.GetBytes(body.Input.GetRawText()));
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    try
                    {
                        var result = await jobs.UpdateJobInputAsync(JobLookup.ByRef(parsed), payload, reason, actorKey, ct);
                        return ToResult("input", parsed, result);
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status413PayloadTooLarge, "Input too large.", ex.Message);
                    }
                }
            )
            .WithSummary("Amend a waiting job's stored input, format-faithfully.")
            .Produces<JobControlResponse>(StatusCodes.Status200OK)
            .Produces<JobControlResponse>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // POST /jobs/{jobRef}/reschedule: unlike the other verbs, the target instant travels in the body
    // rather than being framework-computed, so this uses ReadJsonBodyAsync (mandatory body) instead of
    // MapVerb's optional-reason ReadAsync.
    private static void MapReschedule(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/jobs/{jobRef}/reschedule",
                async Task<IResult> (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.JobRescheduleRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (body!.NextRunAtUtc <= DateTime.MinValue)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "nextRunAtUtc is required."
                        );
                    }

                    var reason = string.IsNullOrWhiteSpace(body.ReasonMessage) ? null : body.ReasonMessage.Trim();
                    if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                    {
                        return reasonError;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await jobs.RescheduleAsync(JobLookup.ByRef(parsed), body.NextRunAtUtc, reason, actorKey, ct);
                    return ToResult("reschedule", parsed, result);
                }
            )
            .WithSummary("Move the job's next-run instant.")
            .Produces<JobControlResponse>(StatusCodes.Status200OK)
            .Produces<JobControlResponse>(StatusCodes.Status409Conflict)
            .Produces<JobControlResponse>(StatusCodes.Status404NotFound);
    }

    // POST /jobs/{jobRef}/reprioritize: like reschedule, the target priority travels in the body, so
    // this uses ReadJsonBodyAsync (mandatory body) instead of MapVerb's optional-reason ReadAsync. An
    // unrecognized priority name fails deserialization inside ReadJsonBodyAsync, which maps it to 400.
    private static void MapReprioritize(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/jobs/{jobRef}/reprioritize",
                async Task<IResult> (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.JobReprioritizeRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    var reason = string.IsNullOrWhiteSpace(body!.ReasonMessage) ? null : body.ReasonMessage.Trim();
                    if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                    {
                        return reasonError;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await jobs.ReprioritizeAsync(JobLookup.ByRef(parsed), body.Priority, reason, actorKey, ct);
                    return ToResult("reprioritize", parsed, result);
                }
            )
            .WithSummary("Change the job's priority.")
            .Produces<JobControlResponse>(StatusCodes.Status200OK)
            .Produces<JobControlResponse>(StatusCodes.Status409Conflict)
            .Produces<JobControlResponse>(StatusCodes.Status404NotFound);
    }

    // POST /jobs/{jobRef}/signals/{signalName}: raise a named signal on a job. This is operator control, so it
    // sits behind EnableControls (only mapped when controls are on) and the confirmation header, like the
    // destructive verbs. The name is validated as user-kebab at the edge: underscores are not valid kebab,
    // so the reserved child-latch "__" names are rejected as malformed, and the sys. reservation is
    // rejected by the same validator. An empty body raises a presence-only signal; a non-empty
    // application/json body is stored verbatim as a JSON payload that the handler's WaitSignalAsync<T>
    // deserializes.
    private static void MapSignal(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/jobs/{jobRef}/signals/{signalName}",
                async (
                    string jobRef,
                    string signalName,
                    HttpContext http,
                    IJobs jobs,
                    IOptions<JobsOptions> jobsOptions,
                    CancellationToken ct
                ) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    try
                    {
                        IdentifierSyntax.ValidateUserKebab(signalName, nameof(signalName), IdentifierSyntax.ExtendedMaxLength);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid signal name.", ex.Message);
                    }

                    var (bytes, error) = await ControlEndpointValidation.ReadOptionalJsonBytesAsync(
                        http,
                        jobsOptions.Value.MaxInlinePayloadBytes,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    // An empty body raises a presence-only signal; a JSON body passes through verbatim.
                    var payload = bytes is null ? JobPayload.None : JobPayload.FromBytes(JobPayloadFormat.Json, bytes);

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;

                    try
                    {
                        var result = await jobs.RaiseSignalAsync(JobLookup.ByRef(parsed), signalName, payload, actorKey, ct);
                        return ToResult("signal", parsed, result);
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status413PayloadTooLarge,
                            "Signal value too large.",
                            ex.Message
                        );
                    }
                }
            )
            .WithSummary("Raise a named signal on the job, with an optional JSON value.")
            .Produces<JobControlResponse>(StatusCodes.Status200OK)
            .Produces<JobControlResponse>(StatusCodes.Status409Conflict)
            .Produces<JobControlResponse>(StatusCodes.Status404NotFound);
    }

    private static void MapVerb(
        RouteGroupBuilder group,
        ActaEndpointOptions options,
        string verb,
        string summary,
        Func<IJobs, JobLookup, string?, string?, CancellationToken, ValueTask<JobControlResult>> invoke
    )
    {
        group
            .MapPost(
                "/jobs/{jobRef}/" + verb,
                async (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                    }

                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    var result = await invoke(jobs, JobLookup.ByRef(parsed), reason, actorKey, ct);
                    return ToResult(verb, parsed, result);
                }
            )
            // One declaration for all five verbs, and it states the transition contract rather than
            // just the happy path: applied is 200, a state that forbids the transition is 409, an
            // unknown ref is 404. All three carry the same body, so a client reads `action` and the
            // resulting `status` without special-casing the status code.
            .WithSummary(summary)
            .Produces<JobControlResponse>(StatusCodes.Status200OK)
            .Produces<JobControlResponse>(StatusCodes.Status409Conflict)
            .Produces<JobControlResponse>(StatusCodes.Status404NotFound);
    }

    private static IResult ToResult(string verb, JobRef jobRef, JobControlResult result)
    {
        var (statusCode, message) = result.Action switch
        {
            ControlAction.Applied => (StatusCodes.Status200OK, $"{Title(verb)} applied."),
            ControlAction.Rejected => (
                StatusCodes.Status409Conflict,
                $"{Title(verb)} rejected: the job's current status does not allow it."
            ),
            _ => (StatusCodes.Status404NotFound, "Job not found."),
        };

        return Results.Json(
            new JobControlResponse(jobRef, result.Action, result.Status, message),
            DashboardJsonContext.Default.JobControlResponse,
            statusCode: statusCode
        );
    }

    private static string Title(string verb) => char.ToUpperInvariant(verb[0]) + verb[1..];
}
