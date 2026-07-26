using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Tenants;

/// <summary>
/// Tenant-control endpoints: registration (insert-or-return-existing), suspend/resume, and the
/// version-guarded metadata patch. Guarded by the shared confirmation header; an invalid tenant key
/// maps to 400.
/// </summary>
internal static class TenantControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group.MapPost(
            "/tenants",
            async Task<IResult> (HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                {
                    return confirmationError;
                }

                var (request, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
                    http,
                    DashboardJsonContext.Default.TenantRegistrationRequest,
                    ct
                );
                if (bodyError is not null)
                {
                    return bodyError;
                }

                var tenantKey = request?.TenantKey?.Trim();
                if (string.IsNullOrEmpty(tenantKey))
                {
                    return ControlEndpointValidation.Problem(
                        StatusCodes.Status400BadRequest,
                        "Invalid tenant request.",
                        "tenantKey is required."
                    );
                }

                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        request!.DisplayName,
                        "displayName",
                        CatalogMetadataLimits.TenantDisplayName
                    ) is
                    { } displayNameError
                )
                {
                    return displayNameError;
                }
                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        request.Description,
                        "description",
                        CatalogMetadataLimits.TenantDescription
                    ) is
                    { } descriptionError
                )
                {
                    return descriptionError;
                }

                try
                {
                    var canonicalTenantKey = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
                    var tenantId = await jobs.Tenants.RegisterAsync(canonicalTenantKey, request.DisplayName, request.Description, ct);
                    return Results.Json(
                        new TenantRegistrationResponse(tenantId, canonicalTenantKey),
                        DashboardJsonContext.Default.TenantRegistrationResponse,
                        statusCode: StatusCodes.Status200OK
                    );
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant request.", ex.Message);
                }
            }
        );

        group.MapPost(
            "/tenants/{key}/suspend",
            async Task<IResult> (string key, HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                if (error is not null)
                {
                    return error;
                }

                try
                {
                    var result = await jobs.Tenants.SuspendAsync(key, reason, http.User?.Identity?.Name, ct);
                    return AdminControlHttp.ToResult(result);
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant key.", ex.Message);
                }
            }
        );

        group.MapPost(
            "/tenants/{key}/resume",
            async Task<IResult> (string key, HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                if (error is not null)
                {
                    return error;
                }

                try
                {
                    var result = await jobs.Tenants.ResumeAsync(key, reason, http.User?.Identity?.Name, ct);
                    return AdminControlHttp.ToResult(result);
                }
                catch (ArgumentException ex)
                {
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant key.", ex.Message);
                }
            }
        );

        group.MapPatch(
            "/tenants/{key}",
            async Task<IResult> (string key, HttpContext http, IJobs jobs, CancellationToken ct) =>
            {
                if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                {
                    return confirmationError;
                }

                var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
                    http,
                    DashboardJsonContext.Default.TenantMetadataPatchRequest,
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
                        "Invalid tenant metadata.",
                        "expectedVersion is required."
                    );
                }
                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        body.DisplayName,
                        "displayName",
                        CatalogMetadataLimits.TenantDisplayName
                    ) is
                    { } displayNameError
                )
                {
                    return displayNameError;
                }
                if (
                    ControlEndpointValidation.ValidateMetadataLength(
                        body.Description,
                        "description",
                        CatalogMetadataLimits.TenantDescription
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
                    var result = await jobs.Tenants.UpdateMetadataAsync(
                        key,
                        body.DisplayName,
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
                    return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant metadata.", ex.Message);
                }
            }
        );
    }
}
