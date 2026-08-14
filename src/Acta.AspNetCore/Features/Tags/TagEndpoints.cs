using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore.Features.Tags;

/// <summary>Request body for a tag upsert: normalized name plus optional preserved value.</summary>
internal sealed record TagUpsertRequest(string? Name, string? Value);

/// <summary>
/// Per-entity tag subresources over <see cref="ITags"/>: GET reads on every taggable dashboard
/// entity, and control-gated POST (upsert) / DELETE (remove) verbs. Patterns and route parameters are
/// spelled out per endpoint so the Request Delegate Generator can bind them; shared logic lives in the
/// read/mutate helpers. A syntactically invalid target reads as 404 (it cannot exist). On mutation an
/// invalid catalog identifier (namespace/tenant/schedule) is a 400, while a malformed job ref is a 404,
/// matching how every other job endpoint treats an unparseable ref.
/// </summary>
internal static class TagEndpoints
{
    public static void MapReads(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // A nested empty-prefix group so the six reads declare their one shared contract once. They
        // all funnel through ReadTags, so they all answer the same two ways.
        var group = outer.MapGroup("");
        group.ProducesJson<IReadOnlyList<TagItem>>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet(
            "/jobs/{jobRef}/tags",
            (string jobRef, IActaOperations operations, CancellationToken ct) => ReadTags(operations, JobTarget(jobRef, options), ct)
        );
        group.MapGet(
            "/definitions/{defId:int}/tags",
            (int defId, IActaOperations operations, CancellationToken ct) =>
                ReadTags(operations, ResolveOrNull(() => TagTarget.ForDefinition(defId)), ct)
        );
        group.MapGet(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
            (string jobNamespace, string jobName, string scheduleName, IActaOperations operations, CancellationToken ct) =>
                ReadTags(operations, ResolveOrNull(() => ScheduleTarget(jobNamespace, jobName, scheduleName)), ct)
        );
        group.MapGet(
            "/workers/{workerId:int}/tags",
            (int workerId, IActaOperations operations, CancellationToken ct) =>
                ReadTags(operations, ResolveOrNull(() => TagTarget.ForWorker(workerId)), ct)
        );
        group.MapGet(
            "/namespaces/{name}/tags",
            (string name, IActaOperations operations, CancellationToken ct) =>
                ReadTags(operations, ResolveOrNull(() => TagTarget.ForNamespace(name)), ct)
        );
        group.MapGet(
            "/tenants/{tenantKey}/tags",
            (string tenantKey, IActaOperations operations, CancellationToken ct) =>
                ReadTags(operations, ResolveOrNull(() => TagTarget.ForTenant(tenantKey)), ct)
        );
    }

    public static void MapControls(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // Same shape for all twelve mutations: every one resolves a target then applies, so an
        // unresolvable or unmatched target is the 404 and an applied change is the AdminControlResponse.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResponse>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost(
            "/jobs/{jobRef}/tags",
            (string jobRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Upsert(http, operations, options, () => JobTarget(jobRef, options), ct)
        );
        group.MapDelete(
            "/jobs/{jobRef}/tags/{tagName}",
            (string jobRef, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Remove(http, operations, options, () => JobTarget(jobRef, options), tagName, ct)
        );

        group.MapPost(
            "/definitions/{defId:int}/tags",
            (int defId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Upsert(http, operations, options, () => TagTarget.ForDefinition(defId), ct)
        );
        group.MapDelete(
            "/definitions/{defId:int}/tags/{tagName}",
            (int defId, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Remove(http, operations, options, () => TagTarget.ForDefinition(defId), tagName, ct)
        );

        group.MapPost(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
            (
                string jobNamespace,
                string jobName,
                string scheduleName,
                HttpContext http,
                IActaOperations operations,
                CancellationToken ct
            ) => Upsert(http, operations, options, () => ScheduleTarget(jobNamespace, jobName, scheduleName), ct)
        );
        group.MapDelete(
            "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags/{tagName}",
            (
                string jobNamespace,
                string jobName,
                string scheduleName,
                string tagName,
                HttpContext http,
                IActaOperations operations,
                CancellationToken ct
            ) => Remove(http, operations, options, () => ScheduleTarget(jobNamespace, jobName, scheduleName), tagName, ct)
        );

        group.MapPost(
            "/workers/{workerId:int}/tags",
            (int workerId, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Upsert(http, operations, options, () => TagTarget.ForWorker(workerId), ct)
        );
        group.MapDelete(
            "/workers/{workerId:int}/tags/{tagName}",
            (int workerId, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Remove(http, operations, options, () => TagTarget.ForWorker(workerId), tagName, ct)
        );

        group.MapPost(
            "/namespaces/{name}/tags",
            (string name, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Upsert(http, operations, options, () => TagTarget.ForNamespace(name), ct)
        );
        group.MapDelete(
            "/namespaces/{name}/tags/{tagName}",
            (string name, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Remove(http, operations, options, () => TagTarget.ForNamespace(name), tagName, ct)
        );

        group.MapPost(
            "/tenants/{tenantKey}/tags",
            (string tenantKey, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Upsert(http, operations, options, () => TagTarget.ForTenant(tenantKey), ct)
        );
        group.MapDelete(
            "/tenants/{tenantKey}/tags/{tagName}",
            (string tenantKey, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                Remove(http, operations, options, () => TagTarget.ForTenant(tenantKey), tagName, ct)
        );
    }

    private static async Task<IResult> ReadTags(IActaOperations operations, TagTarget? target, CancellationToken ct)
    {
        if (target is null)
        {
            return NotFound();
        }

        TagSet? set;
        try
        {
            set = await operations.Tags.GetAsync(target, ct);
        }
        catch (ArgumentException)
        {
            // The service canonicalizes lookup identifiers; a syntactically invalid one cannot exist.
            return NotFound();
        }

        return set is null ? NotFound() : Results.Json(set.Items, DashboardJsonContext.Default.IReadOnlyListTagItem);
    }

    private static async Task<IResult> Upsert(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        Func<TagTarget?> resolve,
        CancellationToken ct
    )
    {
        if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
        {
            return confirmationError;
        }

        var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(http, DashboardJsonContext.Default.TagUpsertRequest, ct);
        if (bodyError is not null)
        {
            return bodyError;
        }

        return string.IsNullOrWhiteSpace(body!.Name)
            ? ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag.", "name is required.")
            : await Mutate(resolve, target => operations.Tags.UpsertAsync(target, new TagInput(body.Name, body.Value), ct: ct));
    }

    private static async Task<IResult> Remove(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        Func<TagTarget?> resolve,
        string tagName,
        CancellationToken ct
    )
    {
        return ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError
            ? confirmationError
            : await Mutate(resolve, target => operations.Tags.RemoveAsync(target, tagName, ct: ct));
    }

    private static async Task<IResult> Mutate(Func<TagTarget?> resolve, Func<TagTarget, ValueTask<TagMutationResult>> apply)
    {
        try
        {
            var target = resolve();
            if (target is null)
            {
                return NotFound();
            }

            var result = await apply(target);
            return result.IsApplied
                ? Results.Json(
                    new AdminControlResponse(AdminControlAction.Applied, null),
                    DashboardJsonContext.Default.AdminControlResponse
                )
                : NotFound();
        }
        catch (ArgumentException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag or tag target.", ex.Message);
        }
    }

    private static TagTarget? JobTarget(string jobRef, ActaEndpointOptions options) =>
        JobTargetBinding.TryParseTarget(jobRef, options, out var lookup) ? TagTarget.ForJob(lookup) : null;

    private static TagTarget ScheduleTarget(string jobNamespace, string jobName, string scheduleName) =>
        TagTarget.ForSchedule(new ScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName));

    /// <summary>Resolve a target for a read, mapping a malformed identifier to null (a 404) rather than a fault.</summary>
    private static TagTarget? ResolveOrNull(Func<TagTarget> factory)
    {
        try
        {
            return factory();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");
}
