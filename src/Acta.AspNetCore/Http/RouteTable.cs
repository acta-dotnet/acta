using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The route shapes a group's table is written in. Each maps a concrete lambda, so the Request Delegate Generator
/// still binds it, declares its shape's responses once, and answers caller errors the API-wide way: a malformed
/// query value, cursor, or route key is a 400, and an addressable target that is not there is a 404.
/// </summary>
internal static class RouteTable
{
    public static RouteHandlerBuilder MapList<TQuery, TResponse>(
        this RouteGroupBuilder group,
        string pattern,
        string summary,
        JsonTypeInfo<TResponse> json,
        Func<QueryReader, TQuery> bind,
        Func<ReadCall, TQuery, ValueTask<TResponse>> list,
        params QueryParameterDoc[] parameters
    )
        where TResponse : IPagedResponse<object> =>
        group
            .MapGet(
                pattern,
                (HttpContext http, IActaOperations operations, IJobs jobs, CancellationToken ct) =>
                    Run(async () =>
                        Results.Json(await list(new ReadCall(operations, jobs, ct), QueryReader.Bind(http.Request, bind)), json)
                    )
            )
            .WithSummary(summary)
            .Produces<TResponse>(StatusCodes.Status200OK)
            .WithQueryParameters(parameters);

    public static RouteHandlerBuilder MapDetail<TDetail>(
        this RouteGroupBuilder group,
        string pattern,
        string summary,
        string entity,
        JsonTypeInfo<TDetail> json,
        Func<HttpContext, ReadCall, ValueTask<TDetail?>> read
    )
        where TDetail : class =>
        group
            .MapGet(
                pattern,
                (HttpContext http, IActaOperations operations, IJobs jobs, CancellationToken ct) =>
                    Run(async () =>
                        await read(http, new ReadCall(operations, jobs, ct)) is { } detail ? Results.Json(detail, json) : NotFound(entity)
                    )
            )
            .WithSummary(summary)
            .Produces<TDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// A target's tags: the read, and when controls are mapped the upsert, whose body is <typeparamref name="TBody"/>, and
    /// the remove. A target the resolver cannot build is caller input and answers 400; an addressable one with no tag set
    /// answers 404, on a mutation with the control envelope.
    /// </summary>
    public static void MapTags<TBody>(
        this RouteGroupBuilder reads,
        RouteGroupBuilder? controls,
        string pattern,
        string entity,
        ActaEndpointOptions options,
        JsonTypeInfo<TBody> upsert,
        Func<HttpContext, TagTarget> target
    )
        where TBody : class, IUpsertTagsRequest
    {
        reads
            .MapGet(
                pattern,
                (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.ReadAsync(operations, () => target(http), ct)
            )
            .WithSummary($"Read the {entity}'s tags.")
            .Produces<GetTagsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        if (controls is null)
        {
            return;
        }

        controls
            .MapPost(
                pattern,
                (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.UpsertAsync(http, operations, options, upsert, () => target(http), ct)
            )
            .AcceptsJson<TBody>()
            .WithSummary($"Add or update one tag on the {entity}.")
            .Produces<UpsertTagsResponse>(StatusCodes.Status200OK)
            .Produces<UpsertTagsResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
        controls
            .MapDelete(
                pattern + "/{tagName}",
                (HttpContext http, IActaOperations operations, CancellationToken ct) =>
                    TagRoutes.RemoveAsync(http, operations, options, () => target(http), RouteKeys.Value(http, "tagName"), ct)
            )
            .WithSummary($"Remove one tag from the {entity}.")
            .Produces<RemoveTagsResponse>(StatusCodes.Status200OK)
            .Produces<RemoveTagsResponse>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A control verb on the target <paramref name="key"/> resolves from the route. It reads the verb's JSON body behind
    /// the confirmation header (optional unless <paramref name="bodyRequired"/>, so a bare POST acts with defaults), trims
    /// and caps its reason, and answers with the verb's response at the status <paramref name="status"/> maps it to. An
    /// argument the facade rejects is caller input, a 400 under <paramref name="invalidTitle"/>.
    /// </summary>
    public static RouteHandlerBuilder MapCommand<TKey, TBody, TResponse>(
        this RouteGroupBuilder controls,
        string pattern,
        string summary,
        ActaEndpointOptions options,
        JsonTypeInfo<TBody> body,
        string invalidTitle,
        ControlStatus<TResponse> status,
        Func<HttpContext, TKey> key,
        Func<ControlCall<TBody>, TKey, ValueTask<TResponse>> apply,
        bool bodyRequired = false
    )
        where TBody : class, IControlRequest
        where TResponse : notnull =>
        DeclareControl<TBody, TResponse>(
            controls.MapPost(
                pattern,
                (HttpContext http, IActaOperations operations, IJobs jobs, CancellationToken ct) =>
                    ControlAsync(
                        new ControlCall<TBody>(http, operations, jobs, null, null, ct),
                        options,
                        body,
                        bodyRequired,
                        invalidTitle,
                        status,
                        key,
                        apply
                    )
            ),
            summary,
            !bodyRequired,
            status
        );

    /// <summary>A PATCH of the target's fields: <see cref="MapCommand"/> with a required body.</summary>
    public static RouteHandlerBuilder MapUpdate<TKey, TBody, TResponse>(
        this RouteGroupBuilder controls,
        string pattern,
        string summary,
        ActaEndpointOptions options,
        JsonTypeInfo<TBody> body,
        string invalidTitle,
        ControlStatus<TResponse> status,
        Func<HttpContext, TKey> key,
        Func<ControlCall<TBody>, TKey, ValueTask<TResponse>> apply
    )
        where TBody : class, IControlRequest
        where TResponse : notnull =>
        DeclareControl<TBody, TResponse>(
            controls.MapPatch(
                pattern,
                (HttpContext http, IActaOperations operations, IJobs jobs, CancellationToken ct) =>
                    ControlAsync(
                        new ControlCall<TBody>(http, operations, jobs, null, null, ct),
                        options,
                        body,
                        true,
                        invalidTitle,
                        status,
                        key,
                        apply
                    )
            ),
            summary,
            optional: false,
            status
        );

    private static RouteHandlerBuilder DeclareControl<TBody, TResponse>(
        RouteHandlerBuilder route,
        string summary,
        bool optional,
        ControlStatus<TResponse> status
    )
        where TBody : notnull
        where TResponse : notnull
    {
        route.AcceptsJson<TBody>(optional).WithSummary(summary).ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
        foreach (var code in status.Codes)
        {
            route.Produces<TResponse>(code);
        }
        return route;
    }

    // The request arrives as a call with no body yet; the verb gets it back with the body read and its reason capped.
    private static async Task<IResult> ControlAsync<TKey, TBody, TResponse>(
        ControlCall<TBody> request,
        ActaEndpointOptions options,
        JsonTypeInfo<TBody> json,
        bool bodyRequired,
        string invalidTitle,
        ControlStatus<TResponse> status,
        Func<HttpContext, TKey> key,
        Func<ControlCall<TBody>, TKey, ValueTask<TResponse>> apply
    )
        where TBody : class, IControlRequest
        where TResponse : notnull
    {
        var (http, ct) = (request.Http, request.Ct);
        TKey target;
        try
        {
            target = key(http);
        }
        catch (MalformedRouteKeyException ex)
        {
            return BadRequest(ex.Message);
        }

        TBody? body;
        IResult? error;
        if (bodyRequired)
        {
            if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
            {
                return confirmationError;
            }

            (body, error) = await ControlEndpointValidation.ReadJsonBodyAsync(http, json, ct);
        }
        else
        {
            (body, error) = await ControlEndpointValidation.ReadOptionalJsonBodyAsync(http, options, json, ct);
        }

        if (error is not null)
        {
            return error;
        }

        var reason = body?.ReasonMessage?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        if (reason is not null && ControlEndpointValidation.ValidateReasonLength(reason, options) is { } reasonError)
        {
            return reasonError;
        }

        try
        {
            var response = await apply(request with { Body = body, ReasonMessage = reason }, target);
            return Control(response, status);
        }
        catch (InvalidControlException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, ex.Title, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, invalidTitle, ex.Message);
        }
    }

    /// <summary>A control response, serialized as its own type, at the status its family maps it to.</summary>
    public static IResult Control<TResponse>(TResponse response, ControlStatus<TResponse> status)
        where TResponse : notnull =>
        Results.Json(response, response.GetType(), DashboardJsonContext.Default, statusCode: status.Map(response));

    /// <summary>
    /// Only the typed validation exceptions are caller errors; anything else, a plain ArgumentException from a
    /// server-side bug included, falls through to the group's sanitized 500.
    /// </summary>
    public static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException ex) when (ex is InvalidPageCursorException or InvalidQueryException or MalformedRouteKeyException)
        {
            return BadRequest(ex.Message);
        }
        catch (TargetNotFoundException ex)
        {
            return NotFound(ex.Entity);
        }
        catch (RouteConflictException ex)
        {
            return ControlEndpointValidation.Problem(StatusCodes.Status409Conflict, ex.Title, ex.Message);
        }
        catch (CalendarUnavailableException ex)
        {
            // The schedule exists; this host cannot evaluate its calendar. The message says which host could.
            return ControlEndpointValidation.Problem(StatusCodes.Status409Conflict, "Calendar not available on this host.", ex.Message);
        }
    }

    /// <summary>The internal id of the job a filter names, or null with no filter; a filter naming no job is the 404.</summary>
    public static async ValueTask<long?> JobIdAsync(this ReadCall call, JobLookup? job) =>
        job is { } lookup ? (await call.Jobs.GetJobIdAsync(lookup, call.Ct))?.JobId ?? throw new TargetNotFoundException("Job") : null;

    public static IResult BadRequest(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: detail);

    /// <summary>The entity is named at every call site: one shared noun would answer every other route with the wrong one.</summary>
    public static IResult NotFound(string entity) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{entity} not found.");
}

/// <summary>One control request as a route table's verb sees it: the parsed body, its trimmed and capped reason, and the operator.</summary>
internal sealed record ControlCall<TBody>(
    HttpContext Http,
    IActaOperations Operations,
    IJobs Jobs,
    TBody? Body,
    string? ReasonMessage,
    CancellationToken Ct
)
    where TBody : class
{
    /// <summary>The authenticated principal, stamped as the operator; never read from the body.</summary>
    public string? Actor => Http.User?.Identity?.Name;
}

/// <summary>What a route table's reads use: the operator facades, and the jobs facade a job ref resolves through.</summary>
internal sealed record ReadCall(IActaOperations Operations, IJobs Jobs, CancellationToken Ct);

/// <summary>
/// The tag subresource over <see cref="ITags"/>, behind every group's tag routes. One rule decides the target's
/// answer: a target this API cannot address, a catalog identifier or an entity ref alike, is malformed input and
/// answers 400, read or mutation; so 404 says only that an addressable target carries no tag set or a mutation
/// matched no row. Every resolver therefore reports a target it cannot build as <see cref="ArgumentException"/>.
/// </summary>
internal static class TagRoutes
{
    public static async Task<IResult> ReadAsync(IActaOperations operations, Func<TagTarget> resolve, CancellationToken ct)
    {
        GetTagsResponse? response;
        try
        {
            // Both the resolver and ITags canonicalize identifiers, and either can report one
            // this API cannot address; that is caller input, so it is the 400 and never a miss.
            response = await operations.Tags.GetAsync(resolve(), ct);
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }

        return response is null ? NotFound() : Results.Json(response, DashboardJsonContext.Default.GetTagsResponse);
    }

    public static async Task<IResult> UpsertAsync<TBody>(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        JsonTypeInfo<TBody> json,
        Func<TagTarget> resolve,
        CancellationToken ct
    )
        where TBody : class, IUpsertTagsRequest
    {
        if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
        {
            return confirmationError;
        }

        var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(http, json, ct);
        if (bodyError is not null)
        {
            return bodyError;
        }

        return string.IsNullOrWhiteSpace(body!.Name)
            ? ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag.", "name is required.")
            : await MutateAsync(
                resolve,
                target => operations.Tags.UpsertAsync(target, new TagInput(body.Name, body.Value), ct: ct),
                DashboardJsonContext.Default.UpsertTagsResponse
            );
    }

    public static async Task<IResult> RemoveAsync(
        HttpContext http,
        IActaOperations operations,
        ActaEndpointOptions options,
        Func<TagTarget> resolve,
        string tagName,
        CancellationToken ct
    ) =>
        ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError
            ? confirmationError
            : await MutateAsync(
                resolve,
                target => operations.Tags.RemoveAsync(target, tagName, ct: ct),
                DashboardJsonContext.Default.RemoveTagsResponse
            );

    // A tag write is a control verb: it answers with its response on 404 exactly as on 200.
    private static async Task<IResult> MutateAsync<T>(Func<TagTarget> resolve, Func<TagTarget, ValueTask<T>> apply, JsonTypeInfo<T> info)
        where T : ITagMutationResponse
    {
        try
        {
            var response = await apply(resolve());
            var status = response.Action == ControlAction.Applied ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
            return Results.Json(response, info, statusCode: status);
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }
    }

    private static IResult InvalidTarget(ArgumentException ex) =>
        ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag or tag target.", ex.Message);

    /// <summary>
    /// A read has no control envelope to answer with - its 200 is the tags themselves - so an unknown target
    /// there is the plain problem document every other read returns.
    /// </summary>
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");
}
