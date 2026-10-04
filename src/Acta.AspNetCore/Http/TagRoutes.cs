using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore;

/// <summary>Request body for a tag upsert: normalized name plus optional preserved value.</summary>
internal sealed record UpsertTagHttpRequest(string? Name, string? Value);

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
        TagSet? set;
        try
        {
            // Both the resolver and ITags canonicalize identifiers, and either can report one
            // this API cannot address; that is caller input, so it is the 400 and never a miss.
            set = await operations.Tags.GetAsync(resolve(), ct);
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }

        return set is null ? NotFound() : Results.Json(set.Items, DashboardJsonContext.Default.IReadOnlyListTagItem);
    }

    public static async Task<IResult> UpsertAsync(
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

        var (body, bodyError) = await ControlEndpointValidation.ReadJsonBodyAsync(
            http,
            DashboardJsonContext.Default.UpsertTagHttpRequest,
            ct
        );
        if (bodyError is not null)
        {
            return bodyError;
        }

        return string.IsNullOrWhiteSpace(body!.Name)
            ? ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag.", "name is required.")
            : await MutateAsync(resolve, target => operations.Tags.UpsertAsync(target, new TagInput(body.Name, body.Value), ct: ct));
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
            : await MutateAsync(resolve, target => operations.Tags.RemoveAsync(target, tagName, ct: ct));

    private static async Task<IResult> MutateAsync<T>(Func<TagTarget> resolve, Func<TagTarget, ValueTask<T>> apply)
        where T : ITagMutationResponse
    {
        try
        {
            var response = await apply(resolve());
            return response.Action == TagMutationAction.Applied
                ? Results.Json(new AdminControlResult(ControlAction.Applied, null), DashboardJsonContext.Default.AdminControlResult)
                : MutationNotFound();
        }
        catch (ArgumentException ex)
        {
            return InvalidTarget(ex);
        }
    }

    private static IResult InvalidTarget(ArgumentException ex) =>
        ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid tag or tag target.", ex.Message);

    /// <summary>
    /// A read has no envelope to answer with - its 200 is the tag list itself - so an unknown target
    /// there is the plain problem document every other read returns.
    /// </summary>
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    /// <summary>
    /// The mutation not-found: a tag mutation is a control verb, so it answers with the family
    /// envelope on 404 exactly as it does on 200. Version is null because a tag write carries no CAS
    /// token - the tag set is the row's own, not a versioned catalog record.
    /// </summary>
    private static IResult MutationNotFound() =>
        Results.Json(
            new AdminControlResult(ControlAction.NotFound, null),
            DashboardJsonContext.Default.AdminControlResult,
            statusCode: StatusCodes.Status404NotFound
        );
}
