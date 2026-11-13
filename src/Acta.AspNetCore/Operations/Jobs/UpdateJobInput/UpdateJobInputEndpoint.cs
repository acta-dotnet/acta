using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// PUT /jobs/{jobRef}/input: replace a job's stored input with the body's envelope. A no-input job has nothing to replace
/// (409); an over-size payload surfaces as 413; an in-flight job is rejected (409) by the verb.
/// </summary>
internal static class UpdateJobInputEndpoint
{
    public static void Map(RouteGroupBuilder controls, ActaEndpointOptions options) =>
        controls
            .MapPut("/jobs/{jobRef}/input", async Task<IResult> (string jobRef, HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                if (!JobRef.TryParse(jobRef, out var parsed))
                {
                    return RefSegment.Malformed("jobRef", "job");
                }

                var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                    http,
                    ActaApiJsonContext.Relaxed.UpdateJobInputRequest,
                    ct
                );
                if (error is not null)
                {
                    return error;
                }

                if (body!.Input is not { } envelope)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid request.", "input is required.");
                }

                JobPayload payload;
                try
                {
                    payload = envelope.ToPayload("input");
                }
                catch (InvalidControlException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, ex.Title, ex.Message);
                }

                var reason = string.IsNullOrWhiteSpace(body.ReasonMessage) ? null : body.ReasonMessage.Trim();
                if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                {
                    return reasonError;
                }

                // Neither early answer reads a status: an unresolvable ref is NotFound, and a job with no stored
                // input (none) is Rejected, since it has nothing to replace.
                var status = ControlStatus.Job<UpdateJobInputResponse>();
                var jobId = (await jobs.GetIdAsync(JobLookup.ByRef(parsed), ct))?.JobId;
                if (jobId is null)
                {
                    return RouteTable.Control(new UpdateJobInputResponse(0, ControlAction.NotFound, null), status);
                }

                if ((await jobs.GetInputAsync(JobLookup.ById(jobId.Value), ct))?.Input is not { } stored)
                {
                    return RouteTable.Control(new UpdateJobInputResponse(jobId.Value, ControlAction.Rejected, null), status);
                }

                // A json input replaces any stored format, and the runner decodes by the new id. Bytes keep the
                // job's own binary format, so the handler's deserializer still reads them.
                if (payload.Format.Id != JobPayloadFormat.Json.Id)
                {
                    if (payload.Format.Id != stored.Format.Id)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Format mismatch.",
                            stored.Format.Id == JobPayloadFormat.Json.Id
                                ? "input.base64 replaces only a binary-format input; this job's input format is json."
                                : $"input.formatId must be this job's input format, {stored.Format.Id} ({stored.Format.Name})."
                        );
                    }

                    payload = JobPayload.CopyBytes(stored.Format, payload.Data.Span);
                }

                // Operator identity for the audit trail comes from the authenticated principal, never the body.
                var actor = http.User?.Identity?.Name;
                try
                {
                    var result = await jobs.UpdateInputAsync(JobLookup.ByRef(parsed), payload, reason, actor, ct);
                    return RouteTable.Control(result, status);
                }
                catch (PayloadTooLargeException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status413PayloadTooLarge, "Input too large.", ex.Message);
                }
            })
            .WithMetadata(new ActaOperationAttribute("updateJobInput", ActaPermissions.Control))
            .WithSummary("Replace the stored input of a job that is not executing.")
            // The body is read manually rather than bound, so the document only learns its shape here.
            .AcceptsJson<UpdateJobInputRequest>()
            .Produces<UpdateJobInputResponse>(StatusCodes.Status200OK)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status409Conflict)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
