namespace Acta;

/// <summary>
/// One row of the retire's single result set: the action, the definition's version after it, and whether the sweep
/// promoted a lane member, all repeated on every row, plus one cancelled job's id and its parent. <c>JobId</c> is null
/// on the single row a retire that cancelled nothing returns.
/// </summary>
[DbProjection(ByName = true)]
internal readonly record struct RetireDefinitionRow(
    DefinitionOverrideAction Action,
    int? Version,
    long? JobId,
    long? ParentId,
    bool LanePromoted
);
