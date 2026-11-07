namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT .../tags/{tagName}</c> on a job, a namespace, a tenant, or a definition: the tag's optional value.
/// The name is the route's, normalized as every tag name is.
/// </summary>
/// <param name="Value">The tag's value; null for a tag that is only a name.</param>
internal sealed record UpsertTagRequest(string? Value = null);
