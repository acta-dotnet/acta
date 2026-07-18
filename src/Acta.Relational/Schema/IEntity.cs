namespace Acta.Relational.Schema;

/// <summary>
/// Marker for a persisted entity. It declares no members; the generator discovers implementations and
/// emits schema and binder, and persistence metadata lives on <c>[DbTable]</c> / <c>[DbColumn]</c> / etc.
/// </summary>
internal interface IEntity { }

/// <summary>
/// Persisted entity with a strongly-typed identity.
/// </summary>
internal interface IEntity<TId> : IEntity
{
    TId Id { get; init; }
}
