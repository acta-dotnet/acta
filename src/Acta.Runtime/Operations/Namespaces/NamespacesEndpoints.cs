using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>The Namespaces routes: the list, the namespace's tags, suspend, resume, and the update.</summary>
internal static class NamespacesEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/namespaces",
            "List namespaces.",
            DashboardJsonContext.Default.PagedResultNamespaceListItem,
            static query => new ListNamespacesQuery(
                NameContains: query.Text("nameContains"),
                Status: query.Enum<NamespaceStatusCode>("status"),
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
        reads.MapTags(controls, "/namespaces/{jobNamespace}/tags", "namespace", options, static http => TagTarget.ForNamespace(Name(http)));
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/namespaces/{jobNamespace}/suspend",
            "Suspend the namespace: new work is rejected at enqueue.",
            options,
            DashboardJsonContext.Default.JobControlRequest,
            "Invalid namespace jobNamespace.",
            ControlStatus.Admin<SuspendNamespaceResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Namespaces.SuspendAsync(name, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/namespaces/{jobNamespace}/resume",
            "Resume a suspended namespace.",
            options,
            DashboardJsonContext.Default.JobControlRequest,
            "Invalid namespace jobNamespace.",
            ControlStatus.Admin<ResumeNamespaceResponse>(StatusCodes.Status200OK, StatusCodes.Status404NotFound),
            Name,
            static (call, name) => call.Operations.Namespaces.ResumeAsync(name, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        // A stale ExpectedVersion is 409 carrying the row's current version, so the caller retries without a re-read.
        controls.MapUpdate(
            "/namespaces/{jobNamespace}",
            "Update the namespace's owner team and description.",
            options,
            DashboardJsonContext.Default.NamespacePatchRequest,
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
                ControlEndpointValidation.RequireLength(body.OwnerTeam, "ownerTeam", AdminTextLimits.NamespaceOwnerTeam);
                ControlEndpointValidation.RequireLength(body.Description, "description", AdminTextLimits.NamespaceDescription);
                return call.Operations.Namespaces.UpdateAsync(
                    name,
                    expectedVersion,
                    body.OwnerTeam,
                    body.Description,
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Ct
                );
            }
        );
    }

    private static string Name(HttpContext http) => RouteKeys.Value(http, "jobNamespace");
}
