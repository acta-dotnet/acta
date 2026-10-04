using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// Body of a <c>PATCH /definitions/{jobNamespace}/{jobName}</c> request: the optimistic-concurrency <c>Version</c> the
/// operator last read, the full override set to apply (a null field clears that override), and an
/// optional note. The actor is stamped server-side from the authenticated principal; the body never
/// carries it.
/// </summary>
internal sealed record SetJobDefinitionOverridesHttpRequest(
    int ExpectedVersion = 0,
    DefinitionPolicyOverrides? Overrides = null,
    string? ReasonMessage = null
) : IControlRequest;

/// <summary>
/// Body of a <c>POST /definitions/{jobNamespace}/{jobName}/retire</c> request: the
/// optimistic-concurrency <c>Version</c> the operator last read and an optional note. The actor is
/// stamped server-side from the authenticated principal; the body never carries it.
/// </summary>
internal sealed record RetireJobDefinitionHttpRequest(int ExpectedVersion = 0, string? ReasonMessage = null) : IControlRequest;

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
            DashboardJsonContext.Default.PagedResultDefinitionListItem,
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
            DashboardJsonContext.Default.DefinitionDetail,
            static (http, call) => call.Operations.Definitions.GetAsync(Key(http).Namespace, Key(http).Name, call.Ct)
        );
        reads.MapTags(controls, "/definitions/{jobNamespace}/{jobName}/tags", "definition", options, static http => TagTarget(http));
        if (controls is null)
        {
            return;
        }

        controls.MapUpdate(
            "/definitions/{jobNamespace}/{jobName}",
            "Set or clear the definition's operator policy overrides.",
            options,
            DashboardJsonContext.Default.SetJobDefinitionOverridesHttpRequest,
            "Invalid definition overrides.",
            ControlStatus.Definition<SetJobDefinitionOverridesResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.UpdateOverridesAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion,
                    call.Body.Overrides ?? new DefinitionPolicyOverrides(),
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Ct
                )
        );
        controls.MapCommand(
            "/definitions/{jobNamespace}/{jobName}/retire",
            "Retire the definition and cancel its parked jobs.",
            options,
            DashboardJsonContext.Default.RetireJobDefinitionHttpRequest,
            "Invalid definition key.",
            ControlStatus.Definition<RetireJobDefinitionResponse>(),
            Key,
            static (call, key) =>
                call.Operations.Definitions.RetireAsync(
                    key.Namespace,
                    key.Name,
                    call.Body!.ExpectedVersion,
                    call.ReasonMessage,
                    call.ActorKey,
                    call.Ct
                ),
            bodyRequired: true
        );
    }

    private static (string Namespace, string Name) Key(HttpContext http) =>
        (RouteKeys.Value(http, "jobNamespace"), RouteKeys.Value(http, "jobName"));

    /// <summary>
    /// <see cref="Acta.TagTarget.ForDefinition"/> folds case for its .NET callers, but the definition read rejects anything
    /// but the canonical lowercase key, so the tag routes apply the read's rule first and answer a bad key the same way.
    /// </summary>
    private static TagTarget TagTarget(HttpContext http)
    {
        var (jobNamespace, jobName) = Key(http);
        IdentifierSyntax.ValidateKebab(jobNamespace, nameof(jobNamespace));
        IdentifierSyntax.ValidateDottedKebab(jobName, nameof(jobName), IdentifierSyntax.ExtendedMaxLength);
        return Acta.TagTarget.ForDefinition(jobNamespace, jobName);
    }
}
