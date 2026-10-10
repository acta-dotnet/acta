using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Definitions routes: the list, one definition, its tags, the overrides update, and retire. A definition is
/// addressed by its natural key (namespace and name); an invalid override throws ArgumentException, the 400.
/// </summary>
internal static class DefinitionsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/definitions",
            "List job definitions.",
            DashboardJsonContext.Default.ListDefinitionsResponse,
            static query => new ListDefinitionsQuery(
                JobNamespace: query.Text("jobNamespace"),
                NameContains: query.Text("nameContains"),
                Status: query.Enum<DefinitionStatusCode>("status"),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor"),
                IncludeTotal: query.Bool("includeTotal") ?? false,
                Tags: query.Tags()
            ),
            static (call, query) => call.Operations.Definitions.ListAsync(query, call.Ct),
            [
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only definitions registered in this namespace."),
                new QueryParameterDoc("nameContains", QueryParameterKind.String, "Case-insensitive substring match on the job name."),
                new QueryParameterDoc(
                    "status",
                    QueryParameterKind.String,
                    "Exact definition status as a kebab code.",
                    CodeKind: "job-definition-status"
                ),
                .. QueryParameterDocExtensions.PagingCore,
                .. QueryParameterDocExtensions.IncludeTotal,
                .. QueryParameterDocExtensions.TagFilter,
            ]
        );
        reads.MapDetail(
            "/definitions/{jobNamespace}/{jobName}",
            "Read one job definition and its effective policy.",
            "Definition",
            DashboardJsonContext.Default.GetDefinitionResponse,
            static (http, call) => call.Operations.Definitions.GetAsync(Key(http).Namespace, Key(http).Name, call.Ct)
        );
        reads.MapTags(
            controls,
            "/definitions/{jobNamespace}/{jobName}/tags",
            "definition",
            options,
            DashboardJsonContext.Default.UpsertDefinitionTagsRequest,
            static http => TagTarget.ForDefinition(Key(http).Namespace, Key(http).Name)
        );
        if (controls is null)
        {
            return;
        }

        controls.MapUpdate(
            "/definitions/{jobNamespace}/{jobName}",
            "Set or clear the definition's operator policy overrides.",
            options,
            DashboardJsonContext.Default.SetDefinitionOverridesRequest,
            "Invalid definition overrides.",
            ControlStatus.Definition<SetDefinitionOverridesResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.UpdateOverridesAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion,
                    call.Body.Overrides ?? new DefinitionPolicyOverrides(),
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                )
        );
        controls.MapCommand(
            "/definitions/{jobNamespace}/{jobName}/retire",
            "Retire the definition and cancel its parked jobs.",
            options,
            DashboardJsonContext.Default.RetireDefinitionRequest,
            "Invalid definition key.",
            ControlStatus.Definition<RetireDefinitionResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.RetireAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion,
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                ),
            bodyRequired: true
        );
    }

    private static (string Namespace, string Name) Key(HttpContext http) =>
        (RouteKeys.Value(http, "jobNamespace"), RouteKeys.Value(http, "jobName"));
}
