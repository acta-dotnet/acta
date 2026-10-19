using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// The paging every list operation shares. A list names itself, its sort order, the kinds of its sort keys,
/// and the filters that shape it, so a cursor it hands out resumes only that list under those filters. The
/// store reads <see cref="Fetch"/> rows after <see cref="After"/>, one past the page, so a full page knows
/// another follows.
/// </summary>
internal sealed class Paging
{
    private readonly string _list;
    private readonly string _order;
    private readonly string _filterHash;

    private Paging(string list, string order, string filterHash, int pageSize, object[]? after)
    {
        _list = list;
        _order = order;
        _filterHash = filterHash;
        PageSize = pageSize;
        After = after;
    }

    public int PageSize { get; }

    /// <summary>The rows to read: one past the page.</summary>
    public int Fetch => PageSize + 1;

    /// <summary>The sort keys of the previous page's last row, in sort order; null on the first page.</summary>
    public object[]? After { get; }

    public static Paging Start(
        string list,
        string order,
        ReadOnlySpan<CursorKeyKind> keys,
        string? cursor,
        int? pageSize,
        ReadOnlySpan<(string Key, string? Value)> filters
    )
    {
        var filterHash = QueryFilterHash.Compute(filters);
        var after = cursor is null ? null : PageCursorCodec.Decode(cursor, list, order, filterHash, keys);
        return new Paging(list, order, filterHash, JobsQueryLimits.NormalizePageSize(pageSize), after);
    }

    /// <summary>
    /// Cuts the rows read to the page and encodes the cursor after its last row: the members of the list's
    /// <see cref="IPagedResponse{T}"/>, which the handler builds its own response from.
    /// </summary>
    public (IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int PageSize, long? TotalCount) Page<T>(
        IReadOnlyList<T> rows,
        long? total,
        Func<T, object[]> keys
    )
    {
        var hasMore = rows.Count > PageSize;
        var items = hasMore ? rows.Take(PageSize).ToList() : rows;
        var next = hasMore ? PageCursorCodec.Encode(_list, _order, _filterHash, keys(items[^1])) : null;
        return (items, next, hasMore, PageSize, total);
    }
}

/// <summary>
/// Canonical hash of a list query's filter values, embedded in cursors so a cursor kept across a
/// filter change is rejected instead of paginating the wrong result set.
/// </summary>
internal static class QueryFilterHash
{
    public static string Compute(ReadOnlySpan<(string Key, string? Value)> filters)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in filters)
        {
            if (value is not null)
            {
                builder.Append(key).Append('=').Append(value).Append('\n');
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
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
