namespace Acta;

/// <summary>
/// Class-level unique index: the shape of <see cref="DbIndexAttribute"/> without descending columns.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbUniqueIndexAttribute : Attribute
{
    /// <summary>
    /// Index name; must carry the `ux_` prefix.
    /// </summary>
    public required string Name { get; init; }
    public required string[] Columns { get; init; }
    public string? Filter { get; init; }

    /// <summary>
    /// Why the index exists: the access pattern it serves, rendered into the data-model docs.
    /// Vocabulary: claim_hot_path, claim_horizon, read_api, dashboard, dashboard_grid, maintenance,
    /// uniqueness, lock_reclaim, scheduler, checkpoint_timer, heartbeat, child_fanout, alert_raise, lane.
    /// Documentation-only; never affects DDL.
    /// </summary>
    public required string Usage { get; init; }
}
