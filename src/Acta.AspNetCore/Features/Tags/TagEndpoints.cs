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
/// read/mutate helpers.
/// </summary>
/// <remarks>
/// One rule decides the target's answer, and it is the API-wide one: a target this API cannot
/// address is malformed input and answers 400, whether it is a catalog identifier
/// (namespace/tenant/schedule/definition) or an entity ref (job, worker, alert), and whether the
/// route reads or mutates. That leaves 404 saying the one thing worth saying - the target was
/// addressable and carries no tag set, or the mutation matched no row - so a caller can tell "you
/// wrote it wrong" from "it is not there" by the status code alone. Every resolver therefore reports
/// a target it cannot build as <see cref="ArgumentException"/>, and the two helpers below turn that
/// into the 400 in one place.
/// </remarks>
internal static class TagEndpoints
{
    public static void MapReads(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // A nested empty-prefix group so the seven reads declare their one shared contract once. They
        // all funnel through ReadTags, so they all answer the same ways: the tag list, the 404 for an
        // addressable target that carries none, and the group-wide 400 for a target that is not
        // addressable at all.
        var group = outer.MapGroup("");
        group.ProducesJson<IReadOnlyList<TagItem>>();
        group.ProducesProblem(StatusCodes.Status404NotFound);

        group
            .MapGet(
                "/jobs/{jobRef}/tags",
                (string jobRef, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, () => JobTarget(jobRef, options), ct)
            )
            .WithSummary("Read the job's tags.");
        group
            .MapGet(
                "/definitions/{jobNamespace}/{jobName}/tags",
                (string jobNamespace, string jobName, IActaOperations operations, CancellationToken ct) =>
                    DefinitionKeyError(jobNamespace, jobName) is { } invalid
                        ? Task.FromResult(invalid)
                        : ReadTags(operations, () => TagTarget.ForDefinition(jobNamespace, jobName), ct)
            )
            .WithSummary("Read the definition's tags.");
        group
            .MapGet(
                "/schedules/{jobNamespace}/{jobName}/{scheduleName}/tags",
                (string jobNamespace, string jobName, string scheduleName, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, () => ScheduleTarget(jobNamespace, jobName, scheduleName), ct)
            )
            .WithSummary("Read the schedule's tags.");
        group
            .MapGet(
                "/workers/{workerRef}/tags",
                (string workerRef, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, () => WorkerTarget(workerRef), ct)
            )
            .WithSummary("Read the worker's tags.");
        group
            .MapGet(
                "/alerts/{alertRef}/tags",
                (string alertRef, IActaOperations operations, CancellationToken ct) => ReadTags(operations, () => AlertTarget(alertRef), ct)
            )
            .WithSummary("Read the alert's tags.");
        group
            .MapGet(
                "/namespaces/{jobNamespace}/tags",
                (string jobNamespace, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, () => TagTarget.ForNamespace(jobNamespace), ct)
            )
            .WithSummary("Read the namespace's tags.");
        group
            .MapGet(
                "/tenants/{tenantKey}/tags",
                (string tenantKey, IActaOperations operations, CancellationToken ct) =>
                    ReadTags(operations, () => TagTarget.ForTenant(tenantKey), ct)
            )
            .WithSummary("Read the tenant's tags.");
    }

    public static void MapControls(RouteGroupBuilder outer, ActaEndpointOptions options)
    {
        // Same shape for all fourteen mutations: every one resolves a target then applies, so an
        // applied change and a target the write matched no row for both answer with the same
        // AdminControlResponse and a client reads `action` without special-casing the status code. A
        // target that is not addressable never gets that far; it is the group-wide 400.
        var group = outer.MapGroup("");
        group.ProducesJson<AdminControlResponse>();
        group.ProducesJson<AdminControlResponse>(StatusCodes.Status404NotFound);

        // The seven upserts read a JSON body and can refuse a content type that is not JSON; the
        // seven removes name their tag in the route and read nothing, so they cannot. That is the one
        // line the two halves do not share, which is the whole reason for the nested group.
        var withBody = group.MapGroup("");
        withBody.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        withBody
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

        withBody
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

        withBody
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

        withBody
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

        withBody
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

        withBody
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

        withBody
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

    private static async Task<IResult> ReadTags(IActaOperations operations, Func<TagTarget> resolve, CancellationToken ct)
    {
        TagSet? set;
        try
        {
            // Both the resolver and the service canonicalize identifiers, and either can report one
            // this API cannot address; that is caller input, so it is the 400 and never a miss.
            set = await operations.Tags.GetAsync(resolve(), ct);
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }

        return set is null ? NotFound() : Results.Json(set.Items, DashboardJsonContext.Default.IReadOnlyListTagItem);
    }

    private static async Task<IResult> Upsert(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        Func<TagTarget> resolve,
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
        Func<TagTarget> resolve,
        string tagName,
        CancellationToken ct
    )
    {
        return ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError
            ? confirmationError
            : await Mutate(resolve, target => operations.Tags.RemoveAsync(target, tagName, ct: ct));
    }

    private static async Task<IResult> Mutate(Func<TagTarget> resolve, Func<TagTarget, ValueTask<TagMutationResult>> apply)
    {
        try
        {
            var result = await apply(resolve());
            return result.IsApplied
                ? Results.Json(
                    new AdminControlResponse(AdminControlAction.Applied, null),
                    DashboardJsonContext.Default.AdminControlResponse
                )
                : MutationNotFound();
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }
    }

    private static IResult InvalidTarget(ArgumentException ex) =>
        ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag or tag target.", ex.Message);

    // The three ref-addressed targets. A ref that does not parse names nothing, and it is caller
    // input, so it reports the same way a malformed catalog identifier already did and the helpers
    // above answer both with the 400. No parameter name on the throw: the message is the wire's
    // detail here, and ArgumentException would append "(Parameter '...')" to it, which would put two
    // spellings of one refusal on the API.
    private static TagTarget JobTarget(string jobRef, ActaEndpointOptions options) =>
        JobTargetBinding.TryParseTarget(jobRef, options, out var lookup)
            ? TagTarget.ForJob(lookup)
            : throw new ArgumentException("jobRef is not a valid job ref.");

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

    private static TagTarget WorkerTarget(string workerRef) =>
        WorkerRef.TryParse(workerRef, out var parsed)
            ? TagTarget.ForWorker(parsed)
            : throw new ArgumentException("workerRef is not a valid worker ref.");

    private static TagTarget AlertTarget(string alertRef) =>
        AlertRef.TryParse(alertRef, out var parsed)
            ? TagTarget.ForAlert(parsed)
            : throw new ArgumentException("alertRef is not a valid alert ref.");

    private static TagTarget ScheduleTarget(string jobNamespace, string jobName, string scheduleName) =>
        TagTarget.ForSchedule(new ScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, jobName), scheduleName));

    // A read has no envelope to answer with - its 200 is the tag list itself - so an unknown target
    // there is the plain problem document every other read returns.
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    /// <summary>
    /// The mutation not-found: a tag mutation is a control verb, so it answers with the family
    /// envelope on 404 exactly as it does on 200. Version is null because a tag write carries no CAS
    /// token - the tag set is the row's own, not a versioned catalog record.
    /// </summary>
    private static IResult MutationNotFound() =>
        Results.Json(
            new AdminControlResponse(AdminControlAction.NotFound, null),
            DashboardJsonContext.Default.AdminControlResponse,
            statusCode: StatusCodes.Status404NotFound
        );
}
