namespace Acta;

/// <summary>
/// Serializer contract for encoding handler inputs, results, and checkpoint payloads to bytes and
/// decoding bytes back to typed values. Built-in serializers occupy <see cref="JobPayloadFormat"/>
/// ids 1 to 3 (<c>json</c>, <c>bytes</c>, <c>text</c>); operator-supplied serializers declare their
/// format via <see cref="JobPayloadFormatDeclarationAttribute"/> and claim ids 128 to 255.
/// </summary>
public interface IJobPayloadSerializer
{
    /// <summary>
    /// The <see cref="JobPayloadFormat"/> this serializer owns. Runtime dispatch keys on
    /// <see cref="JobPayloadFormat.Id"/>; <see cref="JobPayloadFormat.Name"/> is diagnostic metadata.
    /// </summary>
    JobPayloadFormat Format { get; }

    /// <summary>
    /// Serializes <paramref name="value"/> into a <see cref="JobPayload"/> whose <c>Format</c>
    /// matches this serializer.
    /// </summary>
    JobPayload Serialize<T>(T value);

    /// <summary>
    /// Deserializes <paramref name="payload"/> into <typeparamref name="T"/>. Implementations should
    /// compare <c>payload.Format.Id</c> against <c>Format.Id</c> (not value-equality on
    /// <see cref="JobPayloadFormat"/>) to avoid touching <see cref="JobPayloadFormat.Name"/> on the hot path.
    /// </summary>
    T Deserialize<T>(JobPayload payload);
}
