using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// POST job-control endpoints: thin HTTP wrappers over the <see cref="IJobs"/> control verbs. The
/// verbs own transition legality and audit stamping; this layer validates the request shape and
/// maps <see cref="ControlAction"/> to 200 (applied), 409 (rejected or version conflict), and 404
/// (not found).
/// </summary>
internal static class ActaControlEndpoints
{
    public static void Map(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // Every job control verb reads a request body, even the ones whose body is optional, so every
        // one of them can refuse a content type that is not JSON. That is the one error code the API
        // group cannot declare for everybody, because a read never reaches the code that raises it.
        var group = outer.MapGroup("");
        group.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        MapInput(group, options);
        MapSignal(group, options);
    }

    /// <summary>
    /// POST /jobs/{jobRef}/input: amend a job's stored input, round-tripping the job's own payload format.
    /// The body carries exactly one of "input" (raw JSON, stored as json), "text" (stored as text), or
    /// "base64" (stored under the job's current binary format id). The chosen field must match the stored
    /// format, except that "input" is a json fallback for any non-none format (the runner decodes by the
    /// stored id). A no-input job has nothing to amend (409); an over-size payload surfaces as 413; an
    /// in-flight job is rejected (409) by the verb.
    /// </summary>
    private static void MapInput(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/jobs/{jobRef}/input",
                async Task<IResult> (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return RefSegment.Malformed("jobRef", "job");
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
                        body!.Input is { ValueKind: not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null) };
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
                        return ToResult(new UpdateJobInputResponse(0, ControlAction.NotFound, null, null));
                    }

                    var current = await jobs.GetInputAsync(JobLookup.ById(jobId.Value), ct);
                    if (current is not { } stored)
                    {
                        return ToResult(new UpdateJobInputResponse(jobId.Value, ControlAction.Rejected, null, null));
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
                        payload = JobPayload.FromBytes(
                            JobPayloadFormat.Json,
                            System.Text.Encoding.UTF8.GetBytes(body.Input!.Value.GetRawText())
                        );
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actorKey = http.User?.Identity?.Name;
                    try
                    {
                        var result = await jobs.UpdateJobInputAsync(JobLookup.ByRef(parsed), payload, reason, actorKey, ct);
                        return ToResult(result);
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status413PayloadTooLarge, "Input too large.", ex.Message);
                    }
                }
            )
            .WithSummary("Amend a waiting job's stored input.")
            // The body is read manually rather than bound, so the document only learns its shape here.
            .AcceptsJson<JobInputRequest>()
            .Produces<UpdateJobInputResponse>(StatusCodes.Status200OK)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status409Conflict)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// POST /jobs/{jobRef}/signals/{signalName}: raise a named signal on a job. This is operator control, so it
    /// sits behind EnableControls (only mapped when controls are on) and the confirmation header, like the
    /// destructive verbs. The name is validated as user-kebab at the edge: underscores are not valid kebab,
    /// so the reserved child-latch "__" names are rejected as malformed, and the sys. reservation is
    /// rejected by the same validator. An empty body raises a presence-only signal; a non-empty
    /// application/json body is stored verbatim as a JSON payload that the handler's WaitSignalAsync&lt;T&gt;
    /// deserializes.
    /// </summary>
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
                        return RefSegment.Malformed("jobRef", "job");
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
                        return ToResult(result);
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
            // The signal value is free-form JSON passed through verbatim, so the declared body type is
            // "any JSON" rather than a record; optional because an empty body raises a presence-only signal.
            .AcceptsJson<System.Text.Json.JsonElement>(optional: true)
            .Produces<RaiseSignalResponse>(StatusCodes.Status200OK)
            .Produces<RaiseSignalResponse>(StatusCodes.Status409Conflict)
            .Produces<RaiseSignalResponse>(StatusCodes.Status404NotFound);
    }

    // Applied 200, Rejected and VersionConflict 409, NotFound 404, each with the result as the body. The
    // two outcomes /input settles before the verb runs arrive here too: an unresolvable ref is NotFound,
    // and a job with no stored input is Rejected with a null status, since neither read one.
    private static IResult ToResult(IJobControlResponse result) =>
        Results.Json(
            result,
            result.GetType(),
            DashboardJsonContext.Default,
            statusCode: result.Action switch
            {
                ControlAction.Applied => StatusCodes.Status200OK,
                ControlAction.Rejected or ControlAction.VersionConflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status404NotFound,
            }
        );
}
