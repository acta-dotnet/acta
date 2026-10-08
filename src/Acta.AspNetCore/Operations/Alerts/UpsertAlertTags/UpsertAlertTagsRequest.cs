namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /alerts/{alertRef}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertAlertTagsRequest(string? Name, string? Value) : IUpsertTagsRequest;
