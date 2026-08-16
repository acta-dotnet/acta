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
/// invalid catalog identifier (namespace/tenant/schedule/definition) is a 400, while a malformed entity
/// ref (job, worker, alert) is a 404, matching how every other ref-addressed endpoint treats one.
/// </summary>
internal static class TagEndpoints
{
    public static void MapReads(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // A nested empty-prefix group so the seven reads declare their one shared contract once. They
        // all funnel through ReadTags, so they all answer the same two ways.
        var group = outer.MapGroup("");
        group.ProducesJson<IReadOnlyList<TagItem>>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}/tags",
                (string jobRef, IActaOperations operations, CancellationToken ct) => ReadTags(operations, JobTarget(jobRef, options), ct)
            )
            .WithSummary("Read the job's tags.");
        group
            .MapGet(
                "/definitions/{jobNamespace}/{jobName}/tags",
                (string jobNamespace, string jobName, IActaOperations operations, CancellationToken ct) =>
                    DefinitionKeyError(jobNamespace, jobName) is { } invalid
                        ? Task.FromResult(invalid)
                        : ReadTags(operations, TagTarget.ForDefinition(jobNamespace, jobName), ct)
            )
            .WithSummary("Read the definition's tags.");
        group
            .MapGet(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
                (string jobNamespace, string jobName, string scheduleName, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, ResolveOrNull(() => ScheduleTarget(jobNamespace, jobName, scheduleName)), ct)
            )
            .WithSummary("Read the schedule's tags.");
        group
            .MapGet(
                "/workers/{workerRef}/tags",
                (string workerRef, IActaOperations operations, CancellationToken ct) => ReadTags(operations, WorkerTarget(workerRef), ct)
            )
            .WithSummary("Read the worker's tags.");
        group
            .MapGet(
                "/alerts/{alertRef}/tags",
                (string alertRef, IActaOperations operations, CancellationToken ct) => ReadTags(operations, AlertTarget(alertRef), ct)
            )
            .WithSummary("Read the alert's tags.");
        group
            .MapGet(
                "/namespaces/{jobNamespace}/tags",
                (string jobNamespace, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, ResolveOrNull(() => TagTarget.ForNamespace(jobNamespace)), ct)
            )
            .WithSummary("Read the namespace's tags.");
        group
            .MapGet(
                "/tenants/{tenantKey}/tags",
                (string tenantKey, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, ResolveOrNull(() => TagTarget.ForTenant(tenantKey)), ct)
            )
            .WithSummary("Read the tenant's tags.");
    }

    public static void MapControls(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // Same shape for all fourteen mutations: every one resolves a target then applies, so an
        // unresolvable or unmatched target is the 404 and an applied change is the AdminControlResponse.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResponse>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapPost(
                "/jobs/{jobRef}/tags",
                (string jobRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Upsert(http, operations, options, () => JobTarget(jobRef, options), ct)
            )
            // The tag body is read manually rather than bound, so the document only learns its shape
            // from these declarations; one per upsert, the same request record every time.
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the job.");
        group
            .MapDelete(
                "/jobs/{jobRef}/tags/{tagName}",
                (string jobRef, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Remove(http, operations, options, () => JobTarget(jobRef, options), tagName, ct)
            )
            .WithSummary("Remove one tag from the job.");

        group
            .MapPost(
                "/definitions/{jobNamespace}/{jobName}/tags",
                (string jobNamespace, string jobName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    DefinitionKeyError(jobNamespace, jobName) is { } invalid
                        ? Task.FromResult(invalid)
                        : Upsert(http, operations, options, () => TagTarget.ForDefinition(jobNamespace, jobName), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the definition.");
        group
            .MapDelete(
                "/definitions/{jobNamespace}/{jobName}/tags/{tagName}",
                (string jobNamespace, string jobName, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    DefinitionKeyError(jobNamespace, jobName) is { } invalid
                        ? Task.FromResult(invalid)
                        : Remove(http, operations, options, () => TagTarget.ForDefinition(jobNamespace, jobName), tagName, ct)
            )
            .WithSummary("Remove one tag from the definition.");

        group
            .MapPost(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
                (
                    string jobNamespace,
                    string jobName,
                    string scheduleName,
                    HttpContext http,
                    IActaOperations operations,
                    CancellationToken ct
                ) => Upsert(http, operations, options, () => ScheduleTarget(jobNamespace, jobName, scheduleName), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the schedule.");
        group
            .MapDelete(
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
            )
            .WithSummary("Remove one tag from the schedule.");

        group
            .MapPost(
                "/workers/{workerRef}/tags",
                (string workerRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Upsert(http, operations, options, () => WorkerTarget(workerRef), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the worker.");
        group
            .MapDelete(
                "/workers/{workerRef}/tags/{tagName}",
                (string workerRef, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Remove(http, operations, options, () => WorkerTarget(workerRef), tagName, ct)
            )
            .WithSummary("Remove one tag from the worker.");

        group
            .MapPost(
                "/alerts/{alertRef}/tags",
                (string alertRef, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Upsert(http, operations, options, () => AlertTarget(alertRef), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the alert.");
        group
            .MapDelete(
                "/alerts/{alertRef}/tags/{tagName}",
                (string alertRef, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Remove(http, operations, options, () => AlertTarget(alertRef), tagName, ct)
            )
            .WithSummary("Remove one tag from the alert.");

        group
            .MapPost(
                "/namespaces/{jobNamespace}/tags",
                (string jobNamespace, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Upsert(http, operations, options, () => TagTarget.ForNamespace(jobNamespace), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the namespace.");
        group
            .MapDelete(
                "/namespaces/{jobNamespace}/tags/{tagName}",
                (string jobNamespace, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Remove(http, operations, options, () => TagTarget.ForNamespace(jobNamespace), tagName, ct)
            )
            .WithSummary("Remove one tag from the namespace.");

        group
            .MapPost(
                "/tenants/{tenantKey}/tags",
                (string tenantKey, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Upsert(http, operations, options, () => TagTarget.ForTenant(tenantKey), ct)
            )
            .AcceptsJson<TagUpsertRequest>()
            .WithSummary("Add or update one tag on the tenant.");
        group
            .MapDelete(
                "/tenants/{tenantKey}/tags/{tagName}",
                (string tenantKey, string tagName, HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    Remove(http, operations, options, () => TagTarget.ForTenant(tenantKey), tagName, ct)
            )
            .WithSummary("Remove one tag from the tenant.");
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

    /// <summary>
    /// The 400 for a definition key this API cannot address, or null when the key is usable.
    /// <see cref="TagTarget.ForDefinition"/> normalizes for its .NET callers (it folds case), but the
    /// definition reads reject anything but the canonical lowercase form - so the edge applies the
    /// reads' rule first and every definition route, read or write, answers a bad key the same way.
    /// </summary>
    private static IResult? DefinitionKeyError(string jobNamespace, string jobName)
    {
        try
        {
            IdentifierSyntax.ValidateKebab(jobNamespace, nameof(jobNamespace));
            IdentifierSyntax.ValidateDottedKebab(jobName, nameof(jobName), IdentifierSyntax.ExtendedMaxLength);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid definition key.", ex.Message);
        }
    }

    // A malformed entity ref cannot name a row, so it reads as 404 exactly like an unknown job ref.
    private static TagTarget? WorkerTarget(string workerRef) =>
        WorkerRef.TryParse(workerRef, out var parsed) ? TagTarget.ForWorker(parsed) : null;

    private static TagTarget? AlertTarget(string alertRef) =>
        AlertRef.TryParse(alertRef, out var parsed) ? TagTarget.ForAlert(parsed) : null;

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
