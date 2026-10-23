namespace Acta;

/// <summary>
/// Class-level non-unique index. All column lists use snake_case DB names.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbIndexAttribute : Attribute
{
    /// <summary>
    /// Index name; must carry the `ix_` prefix.
    /// </summary>
    public required string Name { get; init; }
    public required string[] Columns { get; init; }
    public string[]? Descending { get; init; }

    /// <summary>
    /// Non-key columns stored in the index leaf so a read it serves never visits the table. SQL Server and PostgreSQL
    /// render <c>INCLUDE</c>; SQLite has no such clause and indexes the key columns alone.
    /// </summary>
    public string[]? Include { get; init; }

    public string? Filter { get; init; }

    /// <summary>
    /// Why the index exists: the access pattern it serves, rendered into the data-model docs.
    /// Vocabulary: claim_hot_path, claim_horizon, read_api, dashboard, dashboard_grid, maintenance,
    /// uniqueness, lock_reclaim, scheduler, checkpoint_timer, heartbeat, child_fanout, alert_raise, lane.
    /// Documentation-only; never affects DDL.
    /// </summary>
    public required string Usage { get; init; }
}
