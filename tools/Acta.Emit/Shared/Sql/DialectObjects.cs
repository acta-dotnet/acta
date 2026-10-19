namespace Acta.Emit.Shared.Sql;

/// <summary>One dialect's routines and operator views, each set in name order, the order a migration carries them in.</summary>
internal sealed record DialectObjects(IReadOnlyList<SqlObject> Routines, IReadOnlyList<SqlObject> Views)
{
    internal IEnumerable<SqlObject> All => Routines.Concat(Views);
}
