using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Acta.Relational.Commands;

/// <summary>
/// Provider-neutral <see cref="DbDataReader"/> readers and scalar-result coercers used by
/// query stores. Reader-extension methods (<c>r.GetDateTimeUtc(n)</c>) coexist with factory
/// funcs that build scalar coercers passed to <c>QueryScalarAsync</c> call sites.
/// </summary>
internal static class DbCellCoercion
{
    public static Func<object?, byte> Byte(string emptyMessage) =>
        value => value is null ? throw new InvalidOperationException(emptyMessage) : Convert.ToByte(value, CultureInfo.InvariantCulture);

    public static Func<object?, int> Int32(string emptyMessage) =>
        value => value is null ? throw new InvalidOperationException(emptyMessage) : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public static Func<object?, short> Int16(string emptyMessage) =>
        value => value is null ? throw new InvalidOperationException(emptyMessage) : Convert.ToInt16(value, CultureInfo.InvariantCulture);

    public static Func<object?, long> Int64(string emptyMessage) =>
        value => value is null ? throw new InvalidOperationException(emptyMessage) : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    public static Func<object?, string> String(string emptyMessage) =>
        value =>
            value is null
                ? throw new InvalidOperationException(emptyMessage)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException(emptyMessage);

    // Postgres/SQL Server return a UTC DateTime (kind Unspecified); SQLite stores instants as epoch
    // milliseconds and returns a long. Normalize both to a Utc-kind DateTime.
    public static Func<object?, DateTime> DateTimeUtc(string emptyMessage) =>
        value => value is null or DBNull ? throw new InvalidOperationException(emptyMessage) : ToUtc(value);

    // Postgres/SQL Server return a DateTime; SQLite returns a long (epoch milliseconds).
    public static DateTime GetDateTimeUtc(this IDataRecord reader, int ordinal) => ToUtc(reader.GetValue(ordinal));

    // The single DB-value -> UTC rule shared by the reader extension, the scalar factory,
    // the query materializer, and DbScalarCoercion.
    public static DateTime ToUtc(object raw) =>
        raw switch
        {
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => DateTime.SpecifyKind(Convert.ToDateTime(raw, CultureInfo.InvariantCulture), DateTimeKind.Utc),
        };

    // SqlServer code columns are tinyint (byte); Postgres are smallint (short).
    public static byte GetByteFromNumeric(this IDataRecord r, int ordinal) =>
        r.GetValue(ordinal) switch
        {
            byte b => b,
            short s => (byte)s,
            int i => (byte)i,
            long l => (byte)l,
            object o => Convert.ToByte(o, CultureInfo.InvariantCulture),
        };

    public static short ToShortOrThrow(object? raw, string message) => Convert.ToInt16(NotNull(raw, message), CultureInfo.InvariantCulture);

    public static int ToIntOrThrow(object? raw, string message) => Convert.ToInt32(NotNull(raw, message), CultureInfo.InvariantCulture);

    public static long ToLongOrThrow(object? raw, string message) => Convert.ToInt64(NotNull(raw, message), CultureInfo.InvariantCulture);

    public static byte ToByteOrThrow(object? raw, string message) => Convert.ToByte(NotNull(raw, message), CultureInfo.InvariantCulture);

    public static long? OptionalInt64(object? raw) => raw is null or DBNull ? null : Convert.ToInt64(raw, CultureInfo.InvariantCulture);

    private static object NotNull(object? raw, string message) =>
        raw is null or DBNull ? throw new InvalidOperationException(message) : raw;

    /// <summary>
    /// Reads the trailing opt-in total of a single-trip list read: advances past the page rows to the
    /// second result set and returns its lone cell as a long, or null when the total was not requested
    /// (the <c>CASE</c> guard yields NULL) or no count row is present. COUNT(*) is bigint on
    /// SQLite/Postgres but int on SQL Server, so the value is coerced either way.
    /// </summary>
    public static async Task<long?> ReadOptionalTotalAsync(DbDataReader reader, CancellationToken ct)
    {
        await reader.NextResultAsync(ct);
        return await reader.ReadAsync(ct) && !await reader.IsDBNullAsync(0, ct)
            ? Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)
            : null;
    }
}
