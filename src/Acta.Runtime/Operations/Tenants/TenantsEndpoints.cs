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
            "List tenants.",
            DashboardJsonContext.Default.PagedResultTenantListItem,
            static query => new ListTenantsQuery(
                NameContains: query.Text("nameContains"),
                Status: query.Enum<TenantStatusCode>("status"),
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
                    "Case-insensitive substring matched against key, display name, or description."
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]
        );
        // A tenant key the facade cannot canonicalize names no tenant, so it reads as not found.
        reads.MapDetail(
            "/tenants/{tenantKey}",
            "Read one tenant.",
            "Tenant",
            DashboardJsonContext.Default.TenantDetail,
            static async (http, operations, ct) =>
            {
                try
                {
                    return await operations.Tenants.GetAsync(Key(http), ct);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        );
        reads.MapTags(controls, "/tenants/{tenantKey}/tags", "tenant", options, static http => TagTarget.ForTenant(Key(http)));
        if (controls is null)
        {
            return;
        }

        RegisterTenantEndpoint.Map(controls, options);
        controls.MapCommand(
            "/tenants/{tenantKey}/suspend",
            "Suspend the tenant: new work is rejected at enqueue.",
            options,
            DashboardJsonContext.Default.JobControlRequest,
            "Invalid tenant key.",
            ControlStatus.Admin<SuspendTenantResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Key,
            static (call, key) => call.Operations.Tenants.SuspendAsync(key, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/tenants/{tenantKey}/resume",
            "Resume a suspended tenant.",
            options,
            DashboardJsonContext.Default.JobControlRequest,
            "Invalid tenant key.",
            ControlStatus.Admin<ResumeTenantResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Key,
            static (call, key) => call.Operations.Tenants.ResumeAsync(key, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries without a re-read.
        controls.MapUpdate(
            "/tenants/{tenantKey}",
            "Update the tenant's display name and description.",
            options,
            DashboardJsonContext.Default.TenantPatchRequest,
            "Invalid tenant update.",
            ControlStatus.Admin<UpdateTenantResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Key,
            static (call, key) =>
            {
                var body = call.Body!;
                var expectedVersion =
                    body.ExpectedVersion ?? throw new InvalidControlException("Invalid tenant update.", "expectedVersion is required.");
                ControlEndpointValidation.RequireLength(body.DisplayName, "displayName", AdminTextLimits.TenantDisplayName);
                ControlEndpointValidation.RequireLength(body.Description, "description", AdminTextLimits.TenantDescription);
                return call.Operations.Tenants.UpdateAsync(
                    key,
                    expectedVersion,
                    body.DisplayName,
                    body.Description,
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Ct
                );
            }
        );
    }

    private static string Key(HttpContext http) => RouteKeys.Value(http, "tenantKey");
}
