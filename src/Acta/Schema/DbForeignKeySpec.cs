namespace Acta;

/// <summary>
/// One enforced foreign-key declaration on an entity, <paramref name="Column"/> to <paramref name="TargetColumn"/>
/// on <paramref name="Target"/> with the provider's <paramref name="OnDelete"/> action; the name carries the
/// <c>fk_</c> prefix. Acta declares enforced FKs only where the child row's lifetime is bounded by the parent's
/// (<c>docs/architecture-decisions.md</c>, "Storage and naming").
/// </summary>
internal sealed record DbForeignKeySpec(string Name, string Column, Type Target, string TargetColumn, DbForeignKeyAction OnDelete);
