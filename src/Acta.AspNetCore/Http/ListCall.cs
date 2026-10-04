namespace Acta.AspNetCore;

/// <summary>What a route table's list reads with: the operator facades and the jobs facade a job filter resolves through.</summary>
internal sealed record ListCall(IActaOperations Operations, IJobs Jobs, CancellationToken Ct);

/// <summary>A filter or key that parsed but names nothing: the 404 for <see cref="Entity"/>.</summary>
internal sealed class TargetNotFoundException(string entity) : Exception($"{entity} not found.")
{
    public string Entity { get; } = entity;
}
