namespace Acta;

/// <summary>
/// <c>ON DELETE</c> action emitted on a foreign key.
/// </summary>
internal enum DbForeignKeyAction
{
    /// <summary>Provider default; deletion of the parent row is rejected if children exist.</summary>
    NoAction,

    /// <summary>Deleting the parent row deletes the child rows in the same transaction.</summary>
    Cascade,

    /// <summary>Deleting the parent row NULLs the child's FK column (requires the column be nullable).</summary>
    SetNull,
}
