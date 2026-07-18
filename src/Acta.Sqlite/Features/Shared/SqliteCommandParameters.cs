using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Acta.Sqlite.Services;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite.Features.Shared;

/// <summary>Provider primitives used by feature stores to bind SQLite command parameters.</summary>
internal static class SqliteCommandParameters
{
    public static void AddText(DbCommand command, string name, string value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Text,
                Value = value,
            }
        );

    public static void AddNullableText(DbCommand command, string name, string? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Text,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    public static void AddInt(DbCommand command, string name, long value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Integer,
                Value = value,
            }
        );

    public static void AddNullableInt(DbCommand command, string name, long? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Integer,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    public static void AddNullableBlob(DbCommand command, string name, byte[]? value) =>
        command.Parameters.Add(
            new SqliteParameter
            {
                ParameterName = name,
                SqliteType = SqliteType.Blob,
                Value = (object?)value ?? DBNull.Value,
            }
        );

    public static string JsonArray<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T, int> writeItem)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < items.Count; i++)
            {
                writer.WriteStartObject();
                writeItem(writer, items[i], i);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    public static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    public static void WriteUtcOrNull(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } instant)
        {
            writer.WriteNumber(name, SqliteDialect.ToUnixMs(instant));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    public static void WriteBase64OrNull(Utf8JsonWriter writer, string name, ReadOnlyMemory<byte>? value)
    {
        if (value is { } bytes)
        {
            writer.WriteString(name, Convert.ToBase64String(bytes.Span));
        }
        else
        {
            writer.WriteNull(name);
        }
    }
}
