using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// POST /jobs/{jobRef}/input: amend a job's stored input, round-tripping the job's own payload format.
/// A no-input job has nothing to amend (409); an over-size payload surfaces as 413; an in-flight job is
/// rejected (409) by the verb.
/// </summary>
internal static class UpdateJobInputEndpoint
{
    public static void Map(RouteGroupBuilder controls, ActaEndpointOptions options) =>
        controls
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
                        DashboardJsonContext.Default.UpdateJobInputRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    var hasInput = body!.Input is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) };
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
                    // round-trips as itself. Neither early answer reads a status: an unresolvable ref is
                    // NotFound, and a job with no stored input (none) is Rejected, since it has nothing to amend.
                    var status = ControlStatus.Job<UpdateJobInputResponse>();
                    var jobId = (await jobs.GetJobIdAsync(JobLookup.ByRef(parsed), ct))?.JobId;
                    if (jobId is null)
                    {
                        return RouteTable.Control(new UpdateJobInputResponse(0, ControlAction.NotFound, null, null), status);
                    }

                    var current = (await jobs.GetInputAsync(JobLookup.ById(jobId.Value), ct))?.Input;
                    if (current is not { } stored)
                    {
                        return RouteTable.Control(new UpdateJobInputResponse(jobId.Value, ControlAction.Rejected, null, null), status);
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

                        try
                        {
                            payload = JobPayload.FromBytes(JobPayloadFormat.Text, Encoding.UTF8.GetBytes(body.Text));
                        }
                        catch (ArgumentException)
                        {
                            return ControlEndpointValidation.Problem(
                                StatusCodes.Status400BadRequest,
                                "Invalid request.",
                                "text cannot contain U+0000."
                            );
                        }
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
                        payload = JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(body.Input!.Value.GetRawText()));
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actor = http.User?.Identity?.Name;
                    try
                    {
                        var result = await jobs.UpdateJobInputAsync(JobLookup.ByRef(parsed), payload, reason, actor, ct);
                        return RouteTable.Control(result, status);
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status413PayloadTooLarge, "Input too large.", ex.Message);
                    }
                }
            )
            .WithSummary("Amend a waiting job's stored input.")
            // The body is read manually rather than bound, so the document only learns its shape here.
            .AcceptsJson<UpdateJobInputRequest>()
            .Produces<UpdateJobInputResponse>(StatusCodes.Status200OK)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status409Conflict)
            .Produces<UpdateJobInputResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
}
