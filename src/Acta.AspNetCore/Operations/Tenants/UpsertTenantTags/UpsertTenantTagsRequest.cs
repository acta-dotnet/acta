namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantName}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertTenantTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
