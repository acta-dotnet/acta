using System.Globalization;
using Acta.Configuration;

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
    /// is <c>DbKind.UtcInstant</c> = Postgres <c>timestamptz</c>, whose binding requires UTC kind), and a
    /// <see cref="byte"/> becomes <see cref="short"/> on Postgres (which has no 1-byte integer).
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
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(t);
            value = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }
        if (provider == DbProvider.Postgres && value is byte b)
        {
            return (short)b;
        }
        return value;
    }
}
