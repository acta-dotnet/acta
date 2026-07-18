using System.Data.Common;
using Acta.Configuration;
using Acta.Relational.Commands;

namespace Acta.Testing.Relational;

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

    /// <summary>
    /// Executes raw SQL for advanced setup, assertions, or diagnostics. The SQL text is formatted with
    /// the Acta schema as <c>{schema}</c>, and parameter values use Acta's provider coercion rules.
    /// </summary>
    public static async Task<int> ExecuteRawAsync(
        this IDbSession db,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var connection = await db.GetConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.Replace("{schema}", db.Schema, StringComparison.OrdinalIgnoreCase);
        foreach (var (name, value) in parameters)
        {
            AddParameter(cmd, db.Provider, name, value);
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameter(DbCommand cmd, DbProvider provider, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = DbValueCoercion.Coerce(value, value?.GetType() ?? typeof(object), provider);
        cmd.Parameters.Add(p);
    }
}
