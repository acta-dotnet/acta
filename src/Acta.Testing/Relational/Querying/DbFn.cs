namespace Acta.Testing.Relational.Querying;

/// <summary>
/// SQL function markers usable only inside an <c>UpdateOnlyAsync</c> set selector. The set-clause
/// builder recognizes these by expression shape and emits the provider's SQL function instead of
/// binding a parameter; the property bodies throw if evaluated in ordinary code.
/// </summary>
internal static class DbFn
{
    /// <summary>
    /// In a set selector, emits the server UTC clock (<c>SYSUTCDATETIME()</c> on SQL Server,
    /// <c>now()</c> on Postgres) for a <c>DbKind.UtcInstant</c> column. Not callable directly.
    /// </summary>
    public static DateTime UtcNow =>
        throw new InvalidOperationException(
            "DbFn.UtcNow is a SQL marker; use it only inside an UpdateOnlyAsync set selector, "
                + "e.g. () => new T { ModifiedAtUtc = DbFn.UtcNow }."
        );
}
