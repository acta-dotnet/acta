using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// Dashboard-depth endpoints: the input-template shape hint (<see cref="MapReads"/>) and the POST /jobs
/// enqueue (<see cref="MapControls"/>). The individual input/result/checkpoint payload reads are gone;
/// the aggregate <c>GET /jobs/{ref}/detail</c> composes them (size-capped) so the whole job screen loads
/// in one request. The input-template read is part of the always-on read surface; the enqueue mutation
/// gates under EnableControls and the control authorizer via <see cref="MapControls"/>.
/// </summary>
internal static class JobDepthEndpoints
{
    public static void MapReads(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        MapInputTemplate(group);
    }

    public static void MapControls(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        MapEnqueue(group, options);
    }

    // GET /jobs/input-template: the enqueue form's shape hint, served from the in-process manifest.
    // A job this host never registered is not an error (a dashboard can point at a shared ledger it
    // has no job assembly for), so it answers 200 with a null template and the form seeds `{}`.
    private static void MapInputTemplate(RouteGroupBuilder group)
    {
        group.MapGet(
            "/jobs/input-template",
            IResult (string? jobNamespace, string? jobName, IJobs jobs) =>
            {
                if (string.IsNullOrWhiteSpace(jobNamespace) || string.IsNullOrWhiteSpace(jobName))
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
                }

                var template = jobs.GetInputTemplate(jobNamespace, jobName);
                JsonElement? shape = null;
                if (template?.TemplateJson is { } json)
                {
                    // Cloned out of the document so the element outlives the using scope.
                    using var document = JsonDocument.Parse(json);
                    shape = document.RootElement.Clone();
                }

                return Results.Json(
                    new JobInputTemplateResponse(
                        jobNamespace,
                        jobName,
                        template?.InputTypeName,
                        template?.InputFormat.Name ?? JobPayloadFormat.None.Name,
                        shape
                    ),
                    DashboardJsonContext.Default.JobInputTemplateResponse
                );
            }
        );
    }

    // POST /jobs: enqueue via IJobs.EnqueueAsync. A namespace/tenant guard rejection surfaces as
    // EnqueueRejectedException, mapped to 409 by the outer group's exception filter.
    private static void MapEnqueue(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group.MapPost(
            "/jobs",
            async Task<IResult> (HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                {
                    return confirmationError;
                }

                var (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(
                    http,
                    DashboardJsonContext.Default.JobEnqueueApiRequest,
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

                var hasInput = body.Input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
                if ((hasInput ? 1 : 0) + (body.Text is not null ? 1 : 0) + (body.Base64 is not null ? 1 : 0) > 1)
                {
                    return ControlEndpointValidation.Problem(
                        StatusCodes.Status400BadRequest,
                        "Invalid request.",
                        "At most one of input, text, or base64 may be supplied."
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
                else if (body.Text is not null)
                {
                    input = JobPayload.FromBytes(JobPayloadFormat.Text, Encoding.UTF8.GetBytes(body.Text));
                }
                else
                {
                    input = hasInput
                        ? JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(body.Input.GetRawText()))
                        : JobPayload.None;
                }

                var request = new JobEnqueueRequest(
                    JobNamespace: body.JobNamespace,
                    JobName: body.JobName,
                    Input: input,
                    DeduplicationKey: body.DeduplicationKey,
                    CorrelationKey: body.CorrelationKey,
                    Priority: body.Priority,
                    NextRunAtUtc: body.NextRunAtUtc,
                    DelaySeconds: body.DelaySeconds,
                    TenantKey: body.TenantKey
                );

                try
                {
                    var outcome = await jobs.EnqueueAsync(request, ct);
                    return Results.Json(
                        new JobEnqueueResponse(outcome.JobRef, outcome.Action),
                        DashboardJsonContext.Default.JobEnqueueResponse,
                        statusCode: StatusCodes.Status201Created
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
        );
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found.");
}
