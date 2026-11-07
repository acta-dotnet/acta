using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>The Namespaces routes: the list, one namespace, its tags, suspend, resume, and the update.</summary>
internal static class NamespacesEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/namespaces",
            new("listNamespaces", ActaPermissions.Read),
            "List namespaces.",
            DashboardJsonContext.Default.ListNamespacesResponse,
            static query => new ListNamespacesQuery(
                NameContains: query.Text("nameContains"),
                Status: query.Code("status", NamespaceStatusCodeExtensions.FromCode),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false,
                Tags: query.Tags()
            ),
            static (call, query) => call.Operations.Namespaces.ListAsync(query, call.Ct),
            [
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact namespace status as a kebab code.",
                    CodeKind: "namespace-status"
                ),
                new QueryParameterDoc("nameContains", QueryParameterKind.String, "Case-insensitive substring match on the namespace name."),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]
        );
        // A namespace name the facade cannot canonicalize is malformed caller input, so it is the 400 and never a miss.
        reads.MapDetail(
            "/namespaces/{jobNamespace}",
            new("getNamespace", ActaPermissions.Read),
            "Read one namespace.",
            "Namespace",
            DashboardJsonContext.Default.GetNamespaceResponse,
            static async (http, call) =>
            {
                try
                {
                    return await call.Operations.Namespaces.GetAsync(Name(http), call.Ct);
                }
                catch (ArgumentException ex)
                {
                    throw new MalformedRouteKeyException(ex.Message);
                }
            }
        );
        reads.MapTags(
            controls,
            "/namespaces/{jobNamespace}/tags",
            "namespace",
            ActaPermissions.Admin,
            options,
            static http => TagTarget.ForNamespace(Name(http)),
            static async (http, call) => await call.Operations.Namespaces.GetAsync(Name(http), call.Ct) is not null
        );
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/namespaces/{jobNamespace}/suspend",
            new("suspendNamespace", ActaPermissions.Admin),
            "Suspend the namespace: new work is rejected at enqueue.",
            options,
            DashboardJsonContext.Default.SuspendNamespaceRequest,
            "Invalid namespace jobNamespace.",
            ControlStatus.Admin<SuspendNamespaceResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Namespaces.SuspendAsync(name, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/namespaces/{jobNamespace}/resume",
            new("resumeNamespace", ActaPermissions.Admin),
            "Resume a suspended namespace.",
            options,
            DashboardJsonContext.Default.ResumeNamespaceRequest,
            "Invalid namespace jobNamespace.",
            ControlStatus.Admin<ResumeNamespaceResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Namespaces.ResumeAsync(name, call.ReasonMessage, call.Actor, call.Ct)
        );
        // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries without a re-read.
        controls.MapUpdate(
            "/namespaces/{jobNamespace}",
            new("updateNamespace", ActaPermissions.Admin),
            "Update the namespace's owner team and description.",
            options,
            DashboardJsonContext.Default.UpdateNamespaceRequest,
            "Invalid namespace update.",
            ControlStatus.Admin<UpdateNamespaceResponse>(
                StatusCodes.Status200OK,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ),
            Name,
            static (call, name) =>
            {
                var body = call.Body!;
                var expectedVersion =
                    body.ExpectedVersion ?? throw new InvalidControlException("Invalid namespace update.", "expectedVersion is required.");
                ControlEndpointValidation.RequireLength(body.OwnerTeam, "ownerTeam", TextLimits.NamespaceOwnerTeam);
                ControlEndpointValidation.RequireLength(body.Description, "description", TextLimits.NamespaceDescription);
                return call.Operations.Namespaces.UpdateAsync(
                    name,
                    expectedVersion,
                    body.OwnerTeam,
                    body.Description,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                );
            }
        );
    }

    private static string Name(HttpContext http) => RouteKeys.Value(http, "jobNamespace");
}
