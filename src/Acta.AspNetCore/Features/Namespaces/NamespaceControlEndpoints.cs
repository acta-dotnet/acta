using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Namespaces;

/// <summary>
/// POST/PATCH namespace-admin endpoints over <see cref="INamespaces"/>. Control-gated by the shared
/// confirmation header; the sys namespace and a malformed jobNamespace both map to 400 via the sibling catch.
/// </summary>
internal static class NamespaceControlEndpoints
{
    public static void Map(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // All three go through AdminControlHttp.ToResult, so all three answer the same two ways with
        // the same body: applied or already-in-state is 200, an unknown target is 404. Only the patch
        // takes an ExpectedVersion, so only the patch declares the 409 below.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResponse>();
        group.ProducesJson<AdminControlResponse>(StatusCodes.Status404NotFound);
        // All three read a body, so all three can refuse a content type that is not JSON.
        group.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group
            .MapPost(
                "/namespaces/{jobNamespace}/suspend",
                async Task<IResult> (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    try
                    {
                        var result = await operations.Namespaces.SuspendAsync(jobNamespace, reason, http.User?.Identity?.Name, ct);
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid namespace jobNamespace.",
                            ex.Message
                        );
                    }
                }
            )
            // The body is read manually rather than bound, so the document only learns its shape from
            // this declaration; optional because a bare POST suspends with no reason.
            .AcceptsJson<JobControlRequest>(optional: true)
            .WithSummary("Suspend the namespace: new work is rejected at enqueue.");

        group
            .MapPost(
                "/namespaces/{jobNamespace}/resume",
                async Task<IResult> (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    try
                    {
                        var result = await operations.Namespaces.ResumeAsync(jobNamespace, reason, http.User?.Identity?.Name, ct);
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid namespace jobNamespace.",
                            ex.Message
                        );
                    }
                }
            )
            .AcceptsJson<JobControlRequest>(optional: true)
            .WithSummary("Resume a suspended namespace.");

        group
            .MapPatch(
                "/namespaces/{jobNamespace}",
                async Task<IResult> (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.NamespacePatchRequest,
                        ct
                    );
                    if (bodyError is not null)
                    {
                        return bodyError;
                    }

                    if (body!.ExpectedVersion is not { } expectedVersion)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid namespace update.",
                            "expectedVersion is required."
                        );
                    }
                    if (
                        ControlEndpointValidation.ValidateLength(body.OwnerTeam, "ownerTeam", AdminTextLimits.NamespaceOwnerTeam) is
                        { } ownerTeamError
                    )
                    {
                        return ownerTeamError;
                    }
                    if (
                        ControlEndpointValidation.ValidateLength(body.Description, "description", AdminTextLimits.NamespaceDescription) is
                        { } descriptionError
                    )
                    {
                        return descriptionError;
                    }

                    var reason = string.IsNullOrWhiteSpace(body.ReasonMessage) ? null : body.ReasonMessage.Trim();
                    if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
                    {
                        return reasonError;
                    }

                    try
                    {
                        var result = await operations.Namespaces.UpdateAsync(
                            jobNamespace,
                            expectedVersion,
                            body.OwnerTeam,
                            body.Description,
                            reason,
                            http.User?.Identity?.Name,
                            ct
                        );
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid namespace update.", ex.Message);
                    }
                }
            )
            .AcceptsJson<NamespacePatchRequest>()
            // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries
            // without a re-read.
            .Produces<AdminControlResponse>(StatusCodes.Status409Conflict)
            .WithSummary("Update the namespace's owner team and description.");
    }
}
