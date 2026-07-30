namespace Acta;

/// <summary>
/// Inert <see cref="IJobPayloadSerializer"/> passed to fabricate-only deserialize delegates on the
/// no-payload branch (<c>InputPayloadFormat = None</c>). Carries the <see cref="JobPayloadFormat.None"/>
/// format; any call to <see cref="Serialize{T}"/> or <see cref="Deserialize{T}"/> indicates the
/// delegate took the wrong branch and throws so the bug surfaces immediately instead of producing
/// silently-wrong bytes.
/// </summary>
internal sealed class NullJobPayloadSerializer : IJobPayloadSerializer
{
    public static readonly NullJobPayloadSerializer Instance = new();

    private NullJobPayloadSerializer() { }

    public JobPayloadFormat Format => JobPayloadFormat.None;

    public JobPayload Serialize<T>(T value) =>
        throw new InvalidOperationException("NullJobPayloadSerializer.Serialize must not be called.");

    public T Deserialize<T>(JobPayload payload) =>
        throw new InvalidOperationException("NullJobPayloadSerializer.Deserialize must not be called.");
}
