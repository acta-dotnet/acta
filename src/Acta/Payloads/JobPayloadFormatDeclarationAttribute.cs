namespace Acta;

/// <summary>
/// Declares a custom payload format on an <see cref="IJobPayloadSerializer"/> implementation.
/// The attribute is named <c>JobPayloadFormatDeclarationAttribute</c> rather than
/// <c>JobPayloadFormatAttribute</c> so the <c>[JobPayloadFormat(...)]</c> shorthand does not shadow
/// the <see cref="JobPayloadFormat"/> value type in attribute-resolution position.
/// </summary>
/// <remarks>
/// The source generator validates the declaration (<c>ACTA0131</c>): id must be 128..255 (custom
/// range), name must be kebab-case and at most 64 chars, ids and names must be unique across the
/// compilation, and the attributed class must implement <see cref="IJobPayloadSerializer"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class JobPayloadFormatDeclarationAttribute(byte id, string name) : Attribute
{
    public byte Id { get; } = id;

    public string Name { get; } = name;
}
