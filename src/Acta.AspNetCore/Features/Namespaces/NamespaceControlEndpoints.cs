using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Namespaces;

/// <summary>
/// POST/PATCH namespace-admin endpoints over <see cref="INamespaces"/>. Control-gated by the shared
/// confirmation header; the sys namespace and a malformed name both map to 400 via the sibling catch.
/// </summary>
internal static class NamespaceControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group.MapPost(
            "/namespaces/{name}/suspend",
            async Task<IResult> (string name, HttpContext http, IActaOperations operations, CancellationToken ct) =>
            {
                var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                if (error is not null)
                {
                    return error;
                }

                try
                {
                    var result = await operations.Namespaces.SuspendAsync(name, reason, http.User?.Identity?.Name, ct);
                    return AdminControlHttp.ToResult(result);
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid namespace name.", ex.Message);
                }
            }
        );

        group.MapPost(
            "/namespaces/{name}/resume",
            async Task<IResult> (string name, HttpContext http, IActaOperations operations, CancellationToken ct) =>
            {
                var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                if (error is not null)
                {
                    return error;
                }

                try
                {
                    var result = await operations.Namespaces.ResumeAsync(name, reason, http.User?.Identity?.Name, ct);
                    return AdminControlHttp.ToResult(result);
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid namespace name.", ex.Message);
                }
            }
        );

        group.MapPatch(
            "/namespaces/{name}",
            async Task<IResult> (string name, HttpContext http, IActaOperations operations, CancellationToken ct) =>
            {
                if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                {
                    return confirmationError;
                }

                var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
                    http,
                    DashboardJsonContext.Default.NamespaceMetadataPatchRequest,
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
                        "Invalid namespace metadata.",
                        "expectedVersion is required."
                    );
                }
                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        body.OwnerTeam,
                        "ownerTeam",
                        CatalogMetadataLimits.NamespaceOwnerTeam
                    ) is
                    { } ownerTeamError
                )
                {
                    return ownerTeamError;
                }
                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        body.Description,
                        "description",
                        CatalogMetadataLimits.NamespaceDescription
                    ) is
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
                    var result = await operations.Namespaces.UpdateMetadataAsync(
                        name,
                        body.OwnerTeam,
                        body.Description,
                        expectedVersion,
                        reason,
                        http.User?.Identity?.Name,
                        ct
                    );
                    return AdminControlHttp.ToResult(result);
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid namespace metadata.", ex.Message);
                }
            }
        );
    }
}
