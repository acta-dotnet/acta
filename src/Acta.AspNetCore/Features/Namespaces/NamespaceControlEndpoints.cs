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
        // All three go through AdminControlHttp.ToResult, so all three answer the same three ways:
        // applied or already-in-state is the response body, an unknown target is 404, and a stale
        // ExpectedVersion is 409.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResponse>();
        group.ProducesProblem(StatusCodes.Status404NotFound);
        group.ProducesProblem(StatusCodes.Status409Conflict);

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
            .WithSummary("Update the namespace's owner team and description.");
    }
}
