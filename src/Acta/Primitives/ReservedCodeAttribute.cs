namespace Acta;

/// <summary>
/// Retains a removed persisted id and textual code as a tombstone. The source generator rejects any
/// active, deprecated, or retired enum member that reuses either identity.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class ReservedCodeAttribute : Attribute
{
    public ReservedCodeAttribute(byte id, string code)
    {
        Id = id;
        Code = code;
    }

    public byte Id { get; }

    public string Code { get; }
}
