namespace Acta;

/// <summary>
/// Retains a removed persisted id and textual code as a tombstone. The source generator rejects any
/// active, deprecated, or retired enum member that reuses either identity.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class ReservedCodeAttribute(byte id, string code) : Attribute
{
    public byte Id { get; } = id;

    public string Code { get; } = code;
}
