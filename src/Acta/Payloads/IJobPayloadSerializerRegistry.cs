namespace Acta;

/// <summary>
/// Resolves an <see cref="IJobPayloadSerializer"/> by its <see cref="JobPayloadFormat.Id"/> byte, populated by
/// <c>j.AddPayloadSerializer&lt;TSerializer&gt;()</c> calls at DI configuration time. Workers route every payload
/// column through this lookup at dispatch, and the row carries its own format id, so wire-format migrations land
/// transparently. Keyed on the byte, not on <see cref="JobPayloadFormat"/> value equality, so hot-path lookups never
/// touch <see cref="JobPayloadFormat.Name"/>.
/// </summary>
public interface IJobPayloadSerializerRegistry
{
    /// <summary>
    /// Resolves the serializer for the given format id. Throws when no serializer is registered.
    /// </summary>
    IJobPayloadSerializer Resolve(byte formatId);

    /// <summary>
    /// True when a serializer for the given format id is registered.
    /// </summary>
    bool IsRegistered(byte formatId);
}
