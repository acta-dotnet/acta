using Microsoft.Data.SqlClient.Server;

namespace Acta.SqlServer.Features.Shared;

/// <summary>Provider primitives used by feature stores to populate SQL Server TVP records.</summary>
internal static class SqlServerCommandParameters
{
    public static void SetNullableString(SqlDataRecord record, int ordinal, string? value)
    {
        if (value is null)
        {
            record.SetDBNull(ordinal);
        }
        else
        {
            record.SetString(ordinal, value);
        }
    }

    public static void SetNullableInt16(SqlDataRecord record, int ordinal, short? value)
    {
        if (value is { } number)
        {
            record.SetInt16(ordinal, number);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    public static void SetNullableInt32(SqlDataRecord record, int ordinal, int? value)
    {
        if (value is { } number)
        {
            record.SetInt32(ordinal, number);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    public static void SetNullableDateTime(SqlDataRecord record, int ordinal, DateTime? value)
    {
        if (value is { } instant)
        {
            record.SetDateTime(ordinal, instant);
        }
        else
        {
            record.SetDBNull(ordinal);
        }
    }

    // SetValue replaces the whole field; SetBytes against a reused record can retain a prior payload tail.
    public static void SetBytesOrNull(SqlDataRecord record, int ordinal, ReadOnlyMemory<byte> data, bool present)
    {
        if (!present)
        {
            record.SetDBNull(ordinal);
            return;
        }

        record.SetValue(ordinal, data.ToArray());
    }
}
