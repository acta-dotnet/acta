namespace Acta;

/// <summary>
/// Class-level enforced foreign key. Acta declares enforced FKs only where the child's lifetime
/// is bounded by the parent's; audit/audit-adjacent tables carry none (see
/// <c>docs/architecture-decisions.md</c>, "Storage and naming").
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbForeignKeyAttribute : Attribute
{
    /// <summary>
    /// Constraint name; must carry the `fk_` prefix.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The referenced entity type.
    /// </summary>
    public required Type Target { get; init; }

    /// <summary>
    /// Snake_case column name on the target entity (validated against the target's [DbColumn] declarations).
    /// </summary>
    public required string TargetColumn { get; init; }

    /// <summary>
    /// Snake_case local column name on the declaring entity.
    /// </summary>
    public required string Column { get; init; }

    /// <summary>
    /// Provider <c>ON DELETE</c> action; FKs that bound a child's lifetime by its parent's
    /// use <see cref="DbForeignKeyAction.Cascade"/>.
    /// </summary>
    public DbForeignKeyAction OnDelete { get; init; } = DbForeignKeyAction.NoAction;
}
