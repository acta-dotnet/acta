using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace Acta;

/// <summary>
/// Wire-format payload: the encoded <see cref="Data"/> of an input, result, checkpoint value, or step result, paired
/// with the <see cref="JobPayloadFormat"/> that routes it to its decoder; <c>Format.IsNone</c> means no payload. A
/// <c>readonly struct</c> since it appears on every payload at scale, built through <see cref="FromBytes"/> (ownership
/// transfer) or <see cref="CopyBytes"/> (defensive copy), which check json bytes, or unchecked through
/// <see cref="Trusted"/> for serializer output and stored rows.
/// </summary>
public readonly struct JobPayload
{
    public JobPayloadFormat Format { get; }

    /// <summary>The encoded payload buffer. Empty when <see cref="Format"/> is <c>None</c>, and may
    /// also be empty for a legitimate empty-array (Bytes) payload.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    public bool IsNone => Format.IsNone;

    public bool IsEmpty => Data.IsEmpty;

    /// <summary>"No payload" sentinel, equivalent to <c>default(JobPayload)</c>.</summary>
    public static JobPayload None => default;

    private JobPayload(JobPayloadFormat format, ReadOnlyMemory<byte> bytes)
    {
        // One-way invariant: a missing payload must not store bytes. Other formats may carry empty
        // data because an empty byte-array payload is legitimate; serializers decide whether they
        // accept empty input.
        if (format.IsNone && !bytes.IsEmpty)
        {
            throw new ArgumentException("Payload format 'none' cannot contain bytes.", nameof(bytes));
        }

        Format = format;
        Data = bytes;
    }

    /// <summary>
    /// Creates a payload by taking ownership of the provided byte array without copying. Json bytes must be exactly
    /// one strict JSON value in UTF-8 (no comments, no trailing comma, no NUL byte), so a handler can read it and the
    /// operator views can decode it; other formats carry any bytes.
    /// </summary>
    /// <remarks>The caller must not mutate <paramref name="ownedBytes"/> afterwards; use <see cref="CopyBytes"/> when it may.</remarks>
    /// <exception cref="ArgumentException">Json bytes that are not one strict JSON value in UTF-8.</exception>
    public static JobPayload FromBytes(JobPayloadFormat format, byte[] ownedBytes)
    {
        ArgumentNullException.ThrowIfNull(ownedBytes);

        CheckJson(format, ownedBytes);
        return new JobPayload(format, ownedBytes);
    }

    /// <summary>
    /// Creates a payload by copying the provided bytes into a new owned buffer, under the same json rule as
    /// <see cref="FromBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Json bytes that are not one strict JSON value in UTF-8.</exception>
    public static JobPayload CopyBytes(JobPayloadFormat format, ReadOnlySpan<byte> bytes)
    {
        CheckJson(format, bytes);
        return new JobPayload(format, bytes.ToArray());
    }

    /// <summary>
    /// A payload whose bytes need no check: a serializer's own output, valid by construction, or a stored row, which
    /// a read must surface as it is rather than refuse.
    /// </summary>
    internal static JobPayload Trusted(JobPayloadFormat format, byte[] ownedBytes) => new(format, ownedBytes);

    // PostgreSQL's operator views decode json through CONVERT_FROM, which fails the whole SELECT on a NUL byte or invalid
    // UTF-8, and a handler cannot read what the strict serializer rejects, so a json payload is checked where it enters.
    private static void CheckJson(JobPayloadFormat format, ReadOnlySpan<byte> bytes)
    {
        if (format.Id != JobPayloadFormat.Json.Id)
        {
            return;
        }
        if (bytes.Contains((byte)0) || !Utf8.IsValid(bytes))
        {
            throw new ArgumentException("A json payload must be valid UTF-8 with no NUL (0x00) byte.", nameof(bytes));
        }

        // One value, read to its end; with the reader's strict defaults the next read throws on a trailing token. Depth
        // stays the serializer's call: a value cannot nest deeper than it has bytes.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = bytes.Length });
        try
        {
            if (reader.Read())
            {
                reader.Skip();
                if (!reader.Read())
                {
                    return;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"A json payload must be exactly one JSON value: {ex.Message}", nameof(bytes), ex);
        }
        throw new ArgumentException("A json payload must be exactly one JSON value.", nameof(bytes));
    }

    /// <summary>
    /// Serializes <paramref name="value"/> through <see cref="JsonJobPayloadSerializer.Default"/>. Use
    /// when the caller doesn't need custom JSON options. Falls back to reflection-based JSON, so under
    /// Native AOT prefer the typed-enqueue path or the <see cref="Json{T}(T, JsonTypeInfo{T})"/> overload.
    /// </summary>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization. Under trimming or Native AOT use Json<T>(T, JsonTypeInfo<T>) or a source-generated JsonSerializerContext wired through UseJsonPayloads."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization. Under trimming or Native AOT use Json<T>(T, JsonTypeInfo<T>) or a source-generated JsonSerializerContext wired through UseJsonPayloads."
    )]
    public static JobPayload Json<T>(T value) => JsonJobPayloadSerializer.Default.Serialize(value);

    /// <summary>
    /// Serializes <paramref name="value"/> with an explicit source-generated <paramref name="typeInfo"/>,
    /// so the payload is built without reflection. The Native-AOT-safe counterpart to
    /// <see cref="Json{T}(T)"/>; the type info comes from an app-supplied <c>JsonSerializerContext</c>.
    /// </summary>
    public static JobPayload Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Trusted(JobPayloadFormat.Json, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    /// <summary>
    /// <see cref="Json{T}(T)"/> for a string, with the framework's own metadata, so a string payload needs neither
    /// reflection nor an app-supplied type info. Empty string produces <c>""</c>.
    /// </summary>
    public static JobPayload Json(string value) => Json(value, ActaSystemJobJsonContext.Default.String);

    /// <summary>
    /// Wraps <paramref name="value"/> as a <see cref="JobPayloadFormat.Bytes"/>-format payload through
    /// <see cref="BytesJobPayloadSerializer.Default"/>. Caller transfers ownership of the array per the
    /// <see cref="FromBytes"/> contract. Empty array is allowed.
    /// </summary>
    public static JobPayload Bytes(byte[] value) => BytesJobPayloadSerializer.Default.Serialize(value);
}
