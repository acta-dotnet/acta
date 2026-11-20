using System.Data.Common;

namespace Acta.Testing;

/// <summary>
/// Advanced testing and diagnostics helpers for the Acta database session.
/// </summary>
internal static class TestConnectionExtensions
{
    /// <summary>
    /// Opens a provider-backed raw ADO connection for setup, assertions, or diagnostics in tests.
    /// Product code should prefer semantic stores; this raw seam is intentionally test-only.
    /// </summary>
    public static Task<DbConnection> GetConnectionAsync(this IDbSession db, CancellationToken ct = default)
    {
        return db.OpenConnectionAsync(ct);
    }

    /// <summary>Acta's table or view <paramref name="name"/> as a statement on this session names it.</summary>
    public static string Qualify(this IDbSession db, string name) => SchemaQualifier.Qualify(db.Provider, db.Schema, name);

    /// <summary>
    /// Executes raw SQL for advanced setup, assertions, or diagnostics. The SQL text names Acta's table <c>t</c> as
    /// <c>{schema}.t</c>, rendered as <see cref="SchemaQualifier"/> names it, and parameter values use Acta's provider
    /// coercion rules. Like the query layer's writes, it runs under the session's transient-conflict retry, in the owned
    /// write queue when the database has one, each attempt in a transaction of its own, so a failed attempt rolls back
    /// whole and the retry starts clean.
    /// </summary>
    public static Task<int> ExecuteRawAsync(
        this IDbSession db,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters
    ) =>
        db.RunWithRetryAsync(
            async token =>
            {
                await using var connection = await db.GetConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = SchemaQualifier.Render(
                    sql.Replace("{schema}", SchemaQualifier.Token, StringComparison.OrdinalIgnoreCase),
                    db.Provider,
                    db.Schema
                );
                foreach (var (name, value) in parameters)
                {
                    AddParameter(cmd, db.Provider, name, value);
                }
                var affected = await cmd.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                return affected;
            },
            ct
        );

    private static void AddParameter(DbCommand cmd, DbProvider provider, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = DbValueCoercion.Coerce(value, value?.GetType() ?? typeof(object), provider);
        DbValueCoercion.ApplyType(p, value?.GetType() ?? typeof(object), provider);
        cmd.Parameters.Add(p);
    }
}
