using System.Data.Common;

namespace Acta.Runtime;

/// <summary>The two shapes most handlers run, each as one call over <see cref="IDbSession"/>.</summary>
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
