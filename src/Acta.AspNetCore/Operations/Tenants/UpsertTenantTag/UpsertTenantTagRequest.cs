namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantName}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertTenantTagRequest(string? Name, string? Value) : IUpsertTagRequest;
