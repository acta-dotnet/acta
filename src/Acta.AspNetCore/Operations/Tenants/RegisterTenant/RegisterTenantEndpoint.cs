using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// HTTP projection of a registered tenant: the echoed canonical key, which is how every other
/// endpoint addresses a tenant. The DB-assigned id stays internal.
/// </summary>
internal sealed record RegisterTenantHttpResponse(string TenantKey);

/// <summary>POST /tenants: registers a tenant, or returns the existing one, by its canonical key.</summary>
internal static class RegisterTenantEndpoint
{
    public static void Map(RouteGroupBuilder controls, ActaEndpointOptions options) =>
        controls
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
                        DashboardJsonContext.Default.RegisterTenantRequest,
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
                            new RegisterTenantHttpResponse(canonicalTenantKey),
                            DashboardJsonContext.Default.RegisterTenantHttpResponse,
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
            .AcceptsJson<RegisterTenantRequest>()
            .Produces<RegisterTenantHttpResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
}
