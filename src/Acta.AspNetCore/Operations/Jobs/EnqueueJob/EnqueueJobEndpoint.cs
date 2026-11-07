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

                    JobPayload input;
                    try
                    {
                        input = body.Input?.ToPayload("input") ?? JobPayload.None;
                    }
                    catch (InvalidControlException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, ex.Title, ex.Message);
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
                        TenantName: body.TenantName,
                        Lane: body.Lane,
                        ConcurrencyKey: body.ConcurrencyKey,
                        Tags: body.Tags
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
            .AcceptsJson<EnqueueJobRequest>(
                example: """
                {
                  "jobNamespace": "billing",
                  "jobName": "send-invoice",
                  "input": { "json": { "invoiceId": 42 } },
                  "deduplicationKey": "invoice-42",
                  "lane": "reports",
                  "concurrencyKey": "customer:42",
                  "tags": [{ "name": "source", "value": "api" }]
                }
                """
            )
            .WithMetadata(
                new ResponseHeaderDoc(
                    "Location",
                    "The job's URL under this mount, {this path}/{jobRef}: on 201 the job this request inserted, on 200 the one its deduplication key matched."
                )
            )
            .Produces<EnqueueJobResponse>(StatusCodes.Status201Created)
            .Produces<EnqueueJobResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
