namespace Acta;

/// <summary>
/// Declares a custom payload format on an <see cref="IJobPayloadSerializer"/> implementation. Named
/// <c>JobPayloadFormatDeclarationAttribute</c> rather than <c>JobPayloadFormatAttribute</c> so the
/// <c>[JobPayloadFormat(...)]</c> shorthand does not shadow the <see cref="JobPayloadFormat"/> value type in
/// attribute-resolution position. The source generator validates it (<c>ACTA0131</c>): the id is 128..255, the name
/// kebab-case and at most 64 chars, ids and names unique across the compilation, and the class implements
/// <see cref="IJobPayloadSerializer"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class JobPayloadFormatDeclarationAttribute(byte id, string name) : Attribute
{
    public byte Id { get; } = id;

    public string Name { get; } = name;
}
