using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Acta;

/// <summary>
/// Provider-neutral <see cref="DbDataReader"/> cell readers used by the handlers and the generated binders.
/// </summary>
internal static class DbCellCoercion
{
    /// <summary>
    /// Postgres/SQL Server return a DateTime; SQLite returns a long (epoch milliseconds).
    /// </summary>
    public static DateTime GetDateTimeUtc(this IDataRecord reader, int ordinal) => ToUtc(reader.GetValue(ordinal));

    /// <summary>
    /// The single DB-value -&gt; UTC rule: Postgres/SQL Server return a UTC DateTime (kind Unspecified), SQLite
    /// returns epoch milliseconds as a long, and both come back as a Utc-kind DateTime.
    /// </summary>
    public static DateTime ToUtc(object raw) =>
        raw switch
        {
            long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => DateTime.SpecifyKind(Convert.ToDateTime(raw, CultureInfo.InvariantCulture), DateTimeKind.Utc),
        };

    /// <summary>
    /// SqlServer code columns are tinyint (byte); Postgres are smallint (short).
    /// </summary>
    public static byte GetByteFromNumeric(this IDataRecord r, int ordinal) =>
        r.GetValue(ordinal) switch
        {
            byte b => b,
            short s => (byte)s,
            int i => (byte)i,
            long l => (byte)l,
            object o => Convert.ToByte(o, CultureInfo.InvariantCulture),
        };

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
