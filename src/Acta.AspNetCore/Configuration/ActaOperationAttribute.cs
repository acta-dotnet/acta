namespace Acta.AspNetCore;

/// <summary>
/// What a mapped Acta route is: the operation it serves and the permission a caller needs for it. Every route of the
/// HTTP API carries one as endpoint metadata, so the OpenAPI document, Acta's permission check, and a host's own
/// conventions and filters read the same two facts from the endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ActaOperationAttribute(string operationId, string permission) : Attribute
{
    /// <summary>The route's OpenAPI operation id, such as <c>pauseJob</c>; the same under any mount.</summary>
    public string OperationId { get; } = operationId;

    /// <summary>The permission the operation needs, one of <see cref="ActaPermissions"/>.</summary>
    public string Permission { get; } = permission;
}
