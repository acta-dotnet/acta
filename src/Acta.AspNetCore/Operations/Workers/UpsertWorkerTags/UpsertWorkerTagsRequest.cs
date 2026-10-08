namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /workers/{workerRef}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertWorkerTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
