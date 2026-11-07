using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>The Tenants routes: the list, one tenant, its tags, registration, suspend, resume, and the update.</summary>
internal static class TenantsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/tenants",
            new("listTenants", ActaPermissions.Read),
            "List tenants.",
            ActaApiJsonContext.Default.ListTenantsResponse,
            static query => new ListTenantsQuery(
                NameContains: query.Text("nameContains"),
                Status: query.Code("status", TenantStatusCodeExtensions.FromCode),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false,
                Tags: query.Tags()
            ),
            static (call, query) => call.Operations.Tenants.ListAsync(query, call.Ct),
            [
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact tenant status as a kebab code.",
                    CodeKind: "tenant-status"
                ),
                new QueryParameterDoc(
                    "nameContains",
                    QueryParameterKind.String,
                    "Case-insensitive substring matched against name, display name, or description."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]
        );
        // A tenant name the facade cannot canonicalize is malformed caller input, so it is the 400 and never a miss.
        reads.MapDetail(
            "/tenants/{tenantName}",
            new("getTenant", ActaPermissions.Read),
            "Read one tenant.",
            "Tenant",
            ActaApiJsonContext.Default.GetTenantResponse,
            static async (http, call) =>
            {
                try
                {
                    return await call.Operations.Tenants.GetAsync(Name(http), call.Ct);
                }
                catch (ArgumentException ex)
                {
                    throw new MalformedRouteKeyException(ex.Message);
                }
            }
        );
        reads.MapTags(
            controls,
            "/tenants/{tenantName}/tags",
            "tenant",
            ActaPermissions.Admin,
            static http => TagTarget.ForTenant(Name(http)),
            static async (http, call) => await call.Operations.Tenants.GetAsync(Name(http), call.Ct) is not null
        );
        if (controls is null)
        {
            return;
        }

        RegisterTenantEndpoint.Map(controls);
        controls.MapCommand(
            "/tenants/{tenantName}/suspend",
            new("suspendTenant", ActaPermissions.Admin),
            "Suspend the tenant: new work is rejected at enqueue.",
            options,
            ActaApiJsonContext.Default.SuspendTenantRequest,
            "Invalid tenant name.",
            ControlStatus.Admin<SuspendTenantResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Tenants.SuspendAsync(name, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/tenants/{tenantName}/resume",
            new("resumeTenant", ActaPermissions.Admin),
            "Resume a suspended tenant.",
            options,
            ActaApiJsonContext.Default.ResumeTenantRequest,
            "Invalid tenant name.",
            ControlStatus.Admin<ResumeTenantResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Tenants.ResumeAsync(name, call.ReasonMessage, call.Actor, call.Ct)
        );
        // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries without a re-read.
        controls.MapUpdate(
            "/tenants/{tenantName}",
            new("updateTenant", ActaPermissions.Admin),
            "Update the tenant's display name and description.",
            options,
            ActaApiJsonContext.Default.UpdateTenantRequest,
            "Invalid tenant update.",
            ControlStatus.Admin<UpdateTenantResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Name,
            static (call, name) =>
            {
                var body = call.Body!;
                var expectedVersion =
                    body.ExpectedVersion ?? throw new InvalidControlException("Invalid tenant update.", "expectedVersion is required.");
                ControlEndpointValidation.RequireLength(body.DisplayName, "displayName", TextLimits.TenantDisplayName);
                ControlEndpointValidation.RequireLength(body.Description, "description", TextLimits.TenantDescription);
                return call.Operations.Tenants.UpdateAsync(
                    name,
                    expectedVersion,
                    body.DisplayName,
                    body.Description,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                );
            }
        );
    }

    private static string Name(HttpContext http) => RouteKeys.Value(http, "tenantName");
}
