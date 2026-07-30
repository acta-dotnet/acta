namespace Acta;

/// <summary>
/// Built-in <see cref="IJobPayloadSerializer"/> for <see cref="JobPayloadFormat.Bytes"/>. A
/// passthrough accepting <c>byte[]</c> and <c>ReadOnlyMemory&lt;byte&gt;</c>; any other input type is
/// a programming error (the descriptor's payload-format inference would not have selected <c>bytes</c>).
/// </summary>
public sealed class BytesJobPayloadSerializer : IJobPayloadSerializer
{
    /// <summary>
    /// Shared stateless instance reused by <see cref="JobPayload.Bytes"/> so callers don't
    /// allocate a fresh serializer per call.
    /// </summary>
    public static BytesJobPayloadSerializer Default { get; } = new();

    public JobPayloadFormat Format => JobPayloadFormat.Bytes;

    public JobPayload Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value), $"BytesJobPayloadSerializer cannot serialize a null {typeof(T).Name}.");
        }

        if (typeof(T) == typeof(byte[]))
        {
            // Caller transfers ownership of the array per the JobPayload.FromBytes contract.
            return JobPayload.FromBytes(Format, (byte[])(object)value);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var mem = (ReadOnlyMemory<byte>)(object)value;
            return JobPayload.CopyBytes(Format, mem.Span);
        }

        throw new InvalidOperationException(
            $"BytesJobPayloadSerializer cannot serialize {typeof(T).FullName}. " + "Expected byte[] or ReadOnlyMemory<byte>."
        );
    }

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"BytesJobPayloadSerializer cannot deserialize payload format '{payload.Format}'.");
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)payload.Data.ToArray();
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            // Fresh copy so the caller can hold the value past the payload's lifetime without
            // worrying about ownership of the backing array.
            return (T)(object)(ReadOnlyMemory<byte>)payload.Data.ToArray();
        }

        throw new InvalidOperationException(
            $"BytesJobPayloadSerializer cannot deserialize into {typeof(T).FullName}. " + "Expected byte[] or ReadOnlyMemory<byte>."
        );
    }
}
