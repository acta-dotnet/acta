using System.Data.Common;

namespace Acta.Runtime;

/// <summary>The shapes most handlers run, each as one call over <see cref="IDbSession"/>.</summary>
internal static class DbSessionExtensions
{
    /// <summary>Runs a read and maps its first row, or returns null when it has none.</summary>
    public static Task<T?> QuerySingleAsync<T>(
        this IDbSession session,
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class =>
        session.QueryAsync(stem, bind, async (reader, token) => await reader.ReadAsync(token) ? mapRow(reader) : null, ct);

    /// <summary>
    /// Runs a list read: every row of the first result set, then the total from a second one, which the SQL
    /// returns only when the caller asked for a count.
    /// </summary>
    public static Task<(IReadOnlyList<T> Rows, long? Total)> QueryPageAsync<T>(
        this IDbSession session,
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    ) =>
        session.QueryAsync<(IReadOnlyList<T>, long?)>(
            stem,
            bind,
            async (reader, token) =>
            {
                var rows = new List<T>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(mapRow(reader));
                }

                return (rows, await DbCellCoercion.ReadOptionalTotalAsync(reader, token));
            },
            ct
        );

    /// <summary>
    /// Runs a write whose SQL always answers one row and maps it. No row is a defect in that SQL, so it
    /// throws naming the command.
    /// </summary>
    public static async Task<T> ExecuteOneAsync<T>(
        this IDbSession session,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class =>
        await session.ExecuteSingleAsync(command, bind, mapRow, ct)
        ?? throw new InvalidOperationException($"{command.Operation} returned no row; its SQL must answer exactly one.");
}
