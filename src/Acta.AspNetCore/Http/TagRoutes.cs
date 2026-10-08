using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>The body of a tag upsert on any target: the tag's name, which is normalized, and its optional value.</summary>
internal interface IUpsertTagsRequest
{
    string? Name { get; }

    string? Value { get; }
}

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
            var status = response.Action == TagMutationAction.Applied ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
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
