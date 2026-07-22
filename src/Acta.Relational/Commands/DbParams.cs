using System.Globalization;
using Acta.Relational.Schema;

namespace Acta.Relational.Commands;

/// <summary>
/// One bound parameter for a provider command. Production code creates these through
/// <c>DbParams.For(ActaSchema...)</c> so generated metadata owns name, kind, width, and scale.
/// </summary>
internal sealed record DbParameterSpec(string Name, object? Value, DbKind Kind, int? Size = null, int? Precision = null, int? Scale = null);

/// <summary>
/// Parameter spec helpers: build a <see cref="DbParameterSpec"/> from generated column /
/// value metadata, coerce its value to the driver-expected CLR type, and validate the spec
/// at bind time so under-specified metadata surfaces deterministically.
/// </summary>
internal static class DbParams
{
    public static DbParameterSpec For<T>(DbColumnSpec<T> spec, object? value) =>
        new(Name: spec.ParameterName, Value: value, Kind: spec.Kind, Size: spec.Size, Precision: spec.Precision, Scale: spec.Scale);

    public static DbParameterSpec For<T>(DbValueSpec<T> spec, T value) =>
        new(Name: spec.ParameterName, Value: value, Kind: spec.Kind, Size: spec.Size, Precision: spec.Precision, Scale: spec.Scale);

    public static object Coerce(DbParameterSpec p)
    {
        if (p.Value is null)
        {
            return DBNull.Value;
        }

        if (p.Kind == DbKind.UtcInstant && p.Value is DateTime dt)
        {
            return ToUtc(dt);
        }

        if (p.Kind == DbKind.Byte && p.Value is not byte)
        {
            return Convert.ToByte(p.Value, CultureInfo.InvariantCulture);
        }
        if (p.Kind == DbKind.Int16 && p.Value is not short)
        {
            return Convert.ToInt16(p.Value, CultureInfo.InvariantCulture);
        }
        if (p.Kind == DbKind.Int32 && p.Value is not int)
        {
            return Convert.ToInt32(p.Value, CultureInfo.InvariantCulture);
        }
        if (p.Kind == DbKind.Int64 && p.Value is not long)
        {
            return Convert.ToInt64(p.Value, CultureInfo.InvariantCulture);
        }

        return p.Value;
    }

    /// <summary>
    /// Normalizes a caller-supplied instant to UTC for the bulk binders, which build provider rows
    /// directly and bypass <see cref="Coerce"/>: Local is converted, Unspecified is tagged UTC.
    /// </summary>
    public static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    public static void Validate(DbParameterSpec p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            throw new InvalidOperationException("SQL parameter name is required.");
        }

        switch (p.Kind)
        {
            case DbKind.AsciiString:
            case DbKind.UnicodeString:
            case DbKind.Bytes:
                if (p.Size is null)
                {
                    throw new InvalidOperationException(
                        $"SQL parameter '{p.Name}' (DbKind.{p.Kind}) requires explicit Size: bind via DbParams.For(...) with generated metadata."
                    );
                }
                break;

            case DbKind.Decimal:
                if (p.Precision is null || p.Scale is null)
                {
                    throw new InvalidOperationException(
                        $"SQL decimal parameter '{p.Name}' requires Precision and Scale: bind via DbParams.For(...) so the column's precision/scale flow through."
                    );
                }
                break;
        }
    }
}
