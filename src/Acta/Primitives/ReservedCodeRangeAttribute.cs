namespace Acta;

/// <summary>
/// Holds a byte-id range outside normal allocation: assigning a member or a tombstone inside a held
/// range is a compile error (ACTA0204). Set <see cref="PermanentlyUnavailable"/> to record that the
/// hold is permanent rather than a releasable reserve; the compile gate treats both the same way.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class ReservedCodeRangeAttribute(byte start, byte end, string reason) : Attribute
{
    public byte Start { get; } = start;

    public byte End { get; } = end;

    public string Reason { get; } = reason;

    public bool PermanentlyUnavailable { get; init; }
}
