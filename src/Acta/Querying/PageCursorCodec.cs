using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Acta.Querying;

/// <summary>
/// Expected CLR shape of one keyset cursor value.
/// </summary>
internal enum CursorKeyKind
{
    Utc,
    Long,
    Int,
    Text,
}

/// <summary>
/// Encodes and decodes the opaque list cursors: versioned JSON (version, operation, order
/// identity, filter hash, keyset values) as base64url. Decode validates every envelope field
/// against the caller's current query so a stale or foreign cursor is rejected with
/// <see cref="InvalidPageCursorException"/> instead of silently returning wrong pages.
/// </summary>
internal static class PageCursorCodec
{
    private const int Version = 1;

    public static string Encode(string operation, string order, string filterHash, IReadOnlyList<object> keys)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("op", operation);
            writer.WriteString("o", order);
            writer.WriteString("f", filterHash);
            writer.WriteStartArray("k");
            foreach (var key in keys)
            {
                switch (key)
                {
                    case DateTime utc:
                        writer.WriteStringValue(utc.ToString("O", CultureInfo.InvariantCulture));
                        break;
                    case long longKey:
                        writer.WriteNumberValue(longKey);
                        break;
                    case int intKey:
                        writer.WriteNumberValue(intKey);
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported cursor key type '{key.GetType().Name}'.");
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    private const int MaxCursorLength = 4096;

    public static object[] Decode(string cursor, string operation, string order, string filterHash, ReadOnlySpan<CursorKeyKind> kinds)
    {
        if (cursor.Length > MaxCursorLength)
        {
            throw new InvalidPageCursorException("Cursor is too large.");
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            throw new InvalidPageCursorException("Cursor is not valid base64url.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new InvalidPageCursorException("Cursor payload is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidPageCursorException("Cursor payload is not a JSON object.");
            }

            RequireNumber(root, "v", Version, "version");
            RequireString(root, "op", operation, "operation");
            RequireString(root, "o", order, "order");
            RequireString(root, "f", filterHash, "filters");

            if (!root.TryGetProperty("k", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidPageCursorException("Cursor carries no keyset values.");
            }
            if (keysElement.GetArrayLength() != kinds.Length)
            {
                throw new InvalidPageCursorException(
                    $"Cursor carries {keysElement.GetArrayLength()} keyset values; expected {kinds.Length}."
                );
            }

            var keys = new object[kinds.Length];
            var index = 0;
            foreach (var element in keysElement.EnumerateArray())
            {
                keys[index] = ReadKey(element, kinds[index], index);
                index++;
            }
            return keys;
        }
    }

    private static object ReadKey(JsonElement element, CursorKeyKind kind, int index)
    {
        switch (kind)
        {
            case CursorKeyKind.Utc when element.ValueKind == JsonValueKind.String:
                if (
                    DateTime.TryParseExact(
                        element.GetString(),
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var utc
                    )
                )
                {
                    return DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc);
                }
                break;
            case CursorKeyKind.Long when element.ValueKind == JsonValueKind.Number:
                if (element.TryGetInt64(out var longKey))
                {
                    return longKey;
                }
                break;
            case CursorKeyKind.Int when element.ValueKind == JsonValueKind.Number:
                if (element.TryGetInt32(out var intKey))
                {
                    return intKey;
                }
                break;
            case CursorKeyKind.Text when element.ValueKind == JsonValueKind.String:
                return element.GetString()!;
        }

        throw new InvalidPageCursorException($"Cursor keyset value {index} is not a valid {kind} value.");
    }

    private static void RequireNumber(JsonElement root, string property, int expected, string label)
    {
        if (
            !root.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value != expected
        )
        {
            throw new InvalidPageCursorException($"Cursor {label} does not match this query.");
        }
    }

    private static void RequireString(JsonElement root, string property, string expected, string label)
    {
        if (
            !root.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || !string.Equals(element.GetString(), expected, StringComparison.Ordinal)
        )
        {
            throw new InvalidPageCursorException($"Cursor {label} does not match this query.");
        }
    }
}
