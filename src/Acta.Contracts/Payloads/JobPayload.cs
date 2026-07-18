using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Acta;

/// <summary>
/// Wire-format payload: the serializer output that lands in <c>Job.Input</c>, <c>JobResult.Result</c>,
/// <c>JobCheckpoint.Value</c> or <c>JobStep.Result</c>. Pairs the encoded
/// <see cref="Data"/> buffer with the <see cref="JobPayloadFormat"/> that produced it so the dispatcher
/// can route the row through the correct decoder. <c>Format.IsNone</c> means there is no payload and the
/// handler returns <c>Task</c> or <c>void</c>.
/// </summary>
/// <remarks>
/// A <c>readonly struct</c> for stack-friendliness, since it appears on every input, result, and
/// checkpoint payload at scale. Construction is gated through <see cref="FromBytes"/> (no copy,
/// ownership transfer) and <see cref="CopyBytes"/> (defensive copy) so the <c>(Format, Data)</c> pair
/// cannot reach an invalid state and the hot path never silently allocates.
/// </remarks>
public readonly struct JobPayload
{
    public JobPayloadFormat Format { get; }

    /// <summary>The encoded payload buffer. Empty when <see cref="Format"/> is <c>None</c>, and may
    /// also be empty for legitimate empty-string (Text) and empty-array (Bytes) payloads.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    public bool IsNone => Format.IsNone;

    public bool IsEmpty => Data.IsEmpty;

    /// <summary>"No payload" sentinel, equivalent to <c>default(JobPayload)</c>.</summary>
    public static JobPayload None => default;

    private JobPayload(JobPayloadFormat format, ReadOnlyMemory<byte> bytes)
    {
        // One-way invariant: a missing payload must not store bytes. Other formats may carry empty
        // data because empty text and empty byte-array payloads are legitimate; serializers decide
        // whether they accept empty input.
        if (format.IsNone && !bytes.IsEmpty)
        {
            throw new ArgumentException("Payload format 'none' cannot contain bytes.", nameof(bytes));
        }

        Format = format;
        Data = bytes;
    }

    /// <summary>
    /// Creates a payload by taking ownership of the provided byte array without copying.
    /// </summary>
    /// <remarks>
    /// The caller must not mutate <paramref name="ownedBytes"/> after passing it to this method.
    /// Use <see cref="CopyBytes"/> when the input buffer cannot be safely transferred.
    /// </remarks>
    public static JobPayload FromBytes(JobPayloadFormat format, byte[] ownedBytes)
    {
        ArgumentNullException.ThrowIfNull(ownedBytes);

        return new JobPayload(format, ownedBytes);
    }

    /// <summary>
    /// Creates a payload by copying the provided bytes into a new owned buffer.
    /// </summary>
    public static JobPayload CopyBytes(JobPayloadFormat format, ReadOnlySpan<byte> bytes)
    {
        return new JobPayload(format, bytes.ToArray());
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
        return FromBytes(JobPayloadFormat.Json, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as UTF-8 through <see cref="TextJobPayloadSerializer.Default"/>.
    /// Empty string is allowed and produces a zero-byte <see cref="JobPayloadFormat.Text"/> payload,
    /// distinct from <see cref="None"/>.
    /// </summary>
    public static JobPayload Text(string value) => TextJobPayloadSerializer.Default.Serialize(value);

    /// <summary>
    /// Wraps <paramref name="value"/> as a <see cref="JobPayloadFormat.Bytes"/>-format payload through
    /// <see cref="BytesJobPayloadSerializer.Default"/>. Caller transfers ownership of the array per the
    /// <see cref="FromBytes"/> contract. Empty array is allowed.
    /// </summary>
    public static JobPayload Bytes(byte[] value) => BytesJobPayloadSerializer.Default.Serialize(value);
}
