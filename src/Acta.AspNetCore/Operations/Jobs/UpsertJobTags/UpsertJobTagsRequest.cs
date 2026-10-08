namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /jobs/{jobRef}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertJobTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
