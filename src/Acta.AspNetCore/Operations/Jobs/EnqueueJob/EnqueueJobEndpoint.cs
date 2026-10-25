using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// POST /jobs: enqueue via IJobs.EnqueueAsync. A namespace/tenant guard rejection surfaces as
/// EnqueueRejectedException, mapped to 409 by the outer group's exception filter.
/// </summary>
internal static class EnqueueJobEndpoint
{
    public static void Map(RouteGroupBuilder controls, ActaEndpointOptions options) =>
        controls
            .MapPost(
                "/jobs",
                async Task<IResult> (HttpContext http, IJobs jobs, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.EnqueueJobRequest,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    if (string.IsNullOrWhiteSpace(body!.JobNamespace) || string.IsNullOrWhiteSpace(body.JobName))
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "jobNamespace and jobName are required."
                        );
                    }

                    var hasInput = body.Input is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) };
                    if (hasInput && body.Base64 is not null)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "At most one of input or base64 may be supplied."
                        );
                    }

                    if (body.FormatId is not null && body.Base64 is null)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "formatId is only valid with base64."
                        );
                    }

                    JobPayload input;
                    if (body.Base64 is not null)
                    {
                        if (body.FormatId is not { } formatId || (formatId != JobPayloadFormat.Bytes.Id && formatId < 128))
                        {
                            return ControlEndpointValidation.Problem(
                                StatusCodes.Status400BadRequest,
                                "Invalid request.",
                                "base64 requires a binary formatId (2 or 128..255)."
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

                        input = JobPayload.FromBytes(JobPayloadFormat.ForId(formatId), decoded);
                    }
                    else
                    {
                        input = hasInput
                            ? JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(body.Input!.Value.GetRawText()))
                            : JobPayload.None;
                    }

                    var request = new Acta.EnqueueJobRequest(
                        JobNamespace: body.JobNamespace,
                        JobName: body.JobName,
                        Input: input,
                        DeduplicationKey: body.DeduplicationKey,
                        CorrelationKey: body.CorrelationKey,
                        Priority: body.Priority,
                        NextRunAtUtc: body.NextRunAtUtc,
                        DelaySeconds: body.DelaySeconds,
                        TenantName: body.TenantName
                    );

                    try
                    {
                        var outcome = await jobs.EnqueueAsync(request, ct);
                        var created = outcome.Action == EnqueueJobAction.Inserted;

                        // Built from this request's own path rather than a link generator: the read lives at
                        // {this path}/{jobRef} under whatever pattern the host mounted, so appending the ref
                        // is correct at any mount and cannot drift from the route.
                        http.Response.Headers.Location = $"{http.Request.PathBase}{http.Request.Path}/{outcome.JobRef}";

                        // 201 means "this request created it". A deduplicated enqueue matched a row that
                        // already existed, so it is a 200 naming the row the caller now shares. Either way
                        // the Location header points at the job, which is the one thing a client reads a
                        // 201 for and which this endpoint previously omitted.
                        return Results.Json(
                            outcome,
                            DashboardJsonContext.Default.EnqueueJobResponse,
                            statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK
                        );
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status413PayloadTooLarge, "Input too large.", ex.Message);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid request.", ex.Message);
                    }
                }
            )
            // 201 when the enqueue inserted, 200 when a deduplication key matched an existing row.
            // Both carry Location; 409 is a namespace or tenant guard refusing the enqueue outright.
            .WithSummary("Enqueue a job.")
            // The body is read manually rather than bound, so the document only learns its shape here.
            .AcceptsJson<EnqueueJobRequest>()
            .Produces<EnqueueJobResponse>(StatusCodes.Status201Created)
            .Produces<EnqueueJobResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
}
