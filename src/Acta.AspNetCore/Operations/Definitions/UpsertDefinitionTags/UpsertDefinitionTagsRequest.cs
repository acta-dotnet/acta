namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /definitions/{jobNamespace}/{jobName}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertDefinitionTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
