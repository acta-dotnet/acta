using System.Globalization;

namespace Acta.Relational.Commands;

/// <summary>
/// Shared CLR-to-parameter value coercion for Acta's fluent table query surface and test-only
/// write helpers. Predicate translation and set-clause building bind values through this so reads
/// and writes treat enums, null, and provider byte handling identically.
/// </summary>
internal static class DbValueCoercion
{
    /// <summary>
    /// Coerce <paramref name="value"/> into the form a <see cref="System.Data.Common.DbParameter"/>
    /// expects: null becomes <see cref="DBNull.Value"/>, enums become their underlying numeric value,
    /// a <see cref="DateTime"/> is normalized to <see cref="DateTimeKind.Utc"/> (every datetime column
    /// is <c>DbKind.UtcInstant</c> = Postgres <c>timestamptz</c>, whose binding requires UTC kind), a
    /// <see cref="Guid"/> becomes its string form on SQLite (which has no uuid type and stores the
    /// column as text), and a <see cref="byte"/> becomes <see cref="short"/> on Postgres (which has no
    /// 1-byte integer).
    /// </summary>
    public static object Coerce(object? value, Type clrType, DbProvider provider)
    {
        if (value is null)
        {
            return DBNull.Value;
        }
        if (value is DateTime dt)
        {
            var utc = dt.Kind switch
            {
                DateTimeKind.Utc => dt,
                DateTimeKind.Local => dt.ToUniversalTime(),
                _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            };
            // SQLite stores instants as epoch milliseconds (INTEGER); bind the same shape the provider
            // writes, not the driver's variable default.
            return provider == DbProvider.Sqlite ? new DateTimeOffset(utc).ToUnixTimeMilliseconds() : utc;
        }
        if (value is Guid guid)
        {
            // Same reason as the instant above, and the same failure if it is left out, but the
            // mismatch is narrower than it looks and worth naming exactly. SQLite has no uuid type, so
            // SqliteDialect.ToSqliteValue writes a DbKind.Guid column as Guid.ToString() - 36
            // characters of lowercase hex and dashes. Microsoft.Data.Sqlite binds a raw Guid parameter
            // as text too, and also 36 characters, but UPPERCASE. SQLite compares text under BINARY
            // collation, so those two never match despite being the same uuid. Nothing errors and
            // nothing is malformed: the predicate simply selects no row, which is the worst way for
            // this to be wrong, because a filter on a ref column reads as "no such job" rather than as
            // a bug. Postgres and SQL Server bind Guid natively to uuid / uniqueidentifier and neither
            // stores text, so the raw value is right there.
            return provider == DbProvider.Sqlite ? guid.ToString() : guid;
        }
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            value = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }
        return provider == DbProvider.Postgres && value is byte b ? (short)b : value;
    }

    /// <summary>
    /// Pins the parameter's type where the driver cannot infer it from the value. A null
    /// <c>byte[]</c> binds as <see cref="DBNull"/>, which SQL Server infers as <c>nvarchar</c> and then
    /// refuses to assign to a <c>varbinary</c> column; the CLR property type is the only thing that
    /// still knows it is binary. Values the driver infers correctly are left alone.
    /// </summary>
    public static void ApplyType(System.Data.Common.DbParameter parameter, Type clrType)
    {
        if ((Nullable.GetUnderlyingType(clrType) ?? clrType) == typeof(byte[]))
        {
            parameter.DbType = System.Data.DbType.Binary;
        }
    }
}
