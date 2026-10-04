namespace Acta.AspNetCore;

/// <summary>What a route table's reads use: the operator facades, and the jobs facade a job ref resolves through.</summary>
internal sealed record ReadCall(IActaOperations Operations, IJobs Jobs, CancellationToken Ct);

/// <summary>A filter or key that parsed but names nothing: the 404 for <see cref="Entity"/>.</summary>
internal sealed class TargetNotFoundException(string entity) : Exception($"{entity} not found.")
{
    public string Entity { get; } = entity;
}

/// <summary>The request is valid but cannot be answered here or now: the 409 under <see cref="Title"/>, with this message as the detail.</summary>
internal sealed class RouteConflictException(string title, string detail) : Exception(detail)
{
    public string Title { get; } = title;
}
