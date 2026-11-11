using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Definitions routes: the list, one definition, its input template, its tags, the overrides update, and retire. A
/// definition is addressed by its natural key (namespace and name); an invalid override throws ArgumentException, the 400.
/// </summary>
internal static class DefinitionsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/definitions",
            new("listDefinitions", ActaPermissions.Read),
            "List job definitions.",
            ActaApiJsonContext.Relaxed.ListDefinitionsResponse,
            static query => new ListDefinitionsQuery(
                JobNamespace: query.Text("jobNamespace"),
                NameContains: query.Text("nameContains"),
                Status: query.Code("status", DefinitionStatusCodeExtensions.FromCode),
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
            new("getDefinition", ActaPermissions.Read),
            "Read one job definition and its effective policy.",
            "Definition",
            ActaApiJsonContext.Relaxed.GetDefinitionResponse,
            static (http, call) => call.Operations.Definitions.GetAsync(Key(http).Namespace, Key(http).Name, call.Ct)
        );
        // The enqueue form's shape hint. A job this host never registered is the 404, which the form reads as no template.
        reads.MapDetail(
            "/definitions/{jobNamespace}/{jobName}/input-template",
            new("getDefinitionInputTemplate", ActaPermissions.Read),
            "Read a JSON template of the definition's input.",
            "Input template",
            ActaApiJsonContext.Relaxed.GetDefinitionInputTemplateResponse,
            static (http, call) => call.Operations.Definitions.GetInputTemplateAsync(Key(http).Namespace, Key(http).Name, call.Ct)
        );
        reads.MapTags(
            controls,
            "/definitions/{jobNamespace}/{jobName}/tags",
            "definition",
            ActaPermissions.Admin,
            static http => TagTarget.ForDefinition(Key(http).Namespace, Key(http).Name),
            static async (http, call) =>
                await call.Operations.Definitions.GetAsync(Key(http).Namespace, Key(http).Name, call.Ct) is not null
        );

        controls.MapUpdate(
            "/definitions/{jobNamespace}/{jobName}/overrides",
            new("updateDefinitionOverrides", ActaPermissions.Admin),
            "Replace the definition's operator policy overrides.",
            options,
            ActaApiJsonContext.Relaxed.UpdateDefinitionOverridesRequest,
            "Invalid definition overrides.",
            ControlStatus.Definition<UpdateDefinitionOverridesResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.UpdateOverridesAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion
                        ?? throw new InvalidControlException("Invalid definition overrides.", "expectedVersion is required."),
                    call.Body.Overrides ?? throw new InvalidControlException("Invalid definition overrides.", "overrides is required."),
                    call.ReasonMessage,
                    call.Actor,
                    call.Ct
                )
        );
        controls.MapCommand(
            "/definitions/{jobNamespace}/{jobName}/retire",
            new("retireDefinition", ActaPermissions.Admin),
            "Retire the definition and cancel its waiting jobs.",
            options,
            ActaApiJsonContext.Relaxed.RetireDefinitionRequest,
            "Invalid definition key.",
            ControlStatus.Definition<RetireDefinitionResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.RetireAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion ?? throw new InvalidControlException("Invalid request.", "expectedVersion is required."),
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
