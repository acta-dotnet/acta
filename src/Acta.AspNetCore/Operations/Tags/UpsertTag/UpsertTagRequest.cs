namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT .../tags/{tagName}</c> on a job, a namespace, a tenant, or a definition: the tag's optional value.
/// The name is the route's, normalized as every tag name is.
/// </summary>
internal sealed record UpsertTagRequest(string? Value = null);
