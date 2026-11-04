namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /namespaces/{jobNamespace}/tags</c>: the tag's name and its optional value.</summary>
internal sealed record UpsertNamespaceTagRequest(string? Name, string? Value) : IUpsertTagRequest;
