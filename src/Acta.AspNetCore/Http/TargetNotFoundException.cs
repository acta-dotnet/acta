namespace Acta.AspNetCore;

/// <summary>A filter or key that parsed but names nothing: the 404 for <see cref="Entity"/>, with its detail when one applies.</summary>
internal sealed class TargetNotFoundException(string entity, string? detail = null) : ActaException($"{entity} not found.")
{
    public string Entity { get; } = entity;

    public string? Detail { get; } = detail;
}
