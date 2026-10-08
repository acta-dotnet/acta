namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantKey}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertTenantTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
