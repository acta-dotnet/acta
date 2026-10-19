namespace Acta.AspNetCore;

/// <summary>A filter or key that parsed but names nothing: the 404 for <see cref="Entity"/>.</summary>
internal sealed class TargetNotFoundException(string entity) : Exception($"{entity} not found.")
{
    public string Entity { get; } = entity;
}
