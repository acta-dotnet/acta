using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Tenants;

/// <summary>
/// Tenant-control endpoints: registration (insert-or-return-existing), suspend/resume, and the
/// version-guarded update patch. Guarded by the shared confirmation header; an invalid tenant key
/// maps to 400.
/// </summary>
internal static class TenantControlEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        group
            .MapPost(
                "/tenants",
                async Task<IResult> (HttpContext http, IActaOperations operations, CancellationToken ct) =>
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
                        ControlEndpointValidation.ValidateLength(request!.DisplayName, "displayName", AdminTextLimits.TenantDisplayName) is
                        { } displayNameError
                    )
                    {
                        return displayNameError;
                    }
                    if (
                        ControlEndpointValidation.ValidateLength(request.Description, "description", AdminTextLimits.TenantDescription) is
                        { } descriptionError
                    )
                    {
                        return descriptionError;
                    }

                    try
                    {
                        var canonicalTenantKey = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
                        await operations.Tenants.RegisterAsync(canonicalTenantKey, request.DisplayName, request.Description, ct);
                        return Results.Json(
                            new TenantRegistrationResponse(canonicalTenantKey),
                            DashboardJsonContext.Default.TenantRegistrationResponse,
                            statusCode: StatusCodes.Status200OK
                        );
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant request.", ex.Message);
                    }
                }
            )
            // RegisterAsync is insert-or-get, and it succeeds either way without saying which, so this
            // cannot honestly claim 201. An idempotent upsert answering 200 with the canonical key is
            // the accurate shape.
            .WithSummary("Register a tenant, or return the existing one.")
            // The body is read manually rather than bound, so the document only learns its shape here.
            .AcceptsJson<TenantRegistrationRequest>()
            .Produces<TenantRegistrationResponse>(StatusCodes.Status200OK);

        group
            .MapPost(
                "/tenants/{tenantKey}/suspend",
                async Task<IResult> (string tenantKey, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    try
                    {
                        var result = await operations.Tenants.SuspendAsync(tenantKey, reason, http.User?.Identity?.Name, ct);
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant key.", ex.Message);
                    }
                }
            )
            .WithSummary("Suspend the tenant: new work is rejected at enqueue.")
            // Optional: a bare POST suspends with no reason.
            .AcceptsJson<JobControlRequest>(optional: true)
            // Applied and not-found carry the same body, so a client reads `action` without
            // special-casing the status code. There is no 409: the verb takes no expected version.
            .Produces<AdminControlResponse>(StatusCodes.Status200OK)
            .Produces<AdminControlResponse>(StatusCodes.Status404NotFound);

        group
            .MapPost(
                "/tenants/{tenantKey}/resume",
                async Task<IResult> (string tenantKey, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    var (reason, error) = await ControlEndpointValidation.ReadAsync(http, options, ct);
                    if (error is not null)
                    {
                        return error;
                    }

                    try
                    {
                        var result = await operations.Tenants.ResumeAsync(tenantKey, reason, http.User?.Identity?.Name, ct);
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant key.", ex.Message);
                    }
                }
            )
            .WithSummary("Resume a suspended tenant.")
            .AcceptsJson<JobControlRequest>(optional: true)
            .Produces<AdminControlResponse>(StatusCodes.Status200OK)
            .Produces<AdminControlResponse>(StatusCodes.Status404NotFound);

        group
            .MapPatch(
                "/tenants/{tenantKey}",
                async Task<IResult> (string tenantKey, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                {
                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
                        http,
                        DashboardJsonContext.Default.TenantPatchRequest,
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
                            "Invalid tenant update.",
                            "expectedVersion is required."
                        );
                    }
                    if (
                        ControlEndpointValidation.ValidateLength(body.DisplayName, "displayName", AdminTextLimits.TenantDisplayName) is
                        { } displayNameError
                    )
                    {
                        return displayNameError;
                    }
                    if (
                        ControlEndpointValidation.ValidateLength(body.Description, "description", AdminTextLimits.TenantDescription) is
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
                        var result = await operations.Tenants.UpdateAsync(
                            tenantKey,
                            expectedVersion,
                            body.DisplayName,
                            body.Description,
                            reason,
                            http.User?.Identity?.Name,
                            ct
                        );
                        return AdminControlHttp.ToResult(result);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tenant update.", ex.Message);
                    }
                }
            )
            .WithSummary("Update the tenant's display name and description.")
            .AcceptsJson<TenantPatchRequest>()
            // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries
            // without a re-read.
            .Produces<AdminControlResponse>(StatusCodes.Status200OK)
            .Produces<AdminControlResponse>(StatusCodes.Status404NotFound)
            .Produces<AdminControlResponse>(StatusCodes.Status409Conflict);
    }
}
