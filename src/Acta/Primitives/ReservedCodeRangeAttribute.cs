namespace Acta;

/// <summary>
/// Holds a byte-id range outside normal allocation. Held ranges are reported separately from
/// consumed capacity; set <see cref="PermanentlyUnavailable"/> when every id in the range is
/// permanently consumed and can never be assigned.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class ReservedCodeRangeAttribute(byte start, byte end, string reason) : Attribute
{
    public byte Start { get; } = start;

    public byte End { get; } = end;

    public string Reason { get; } = reason;

    public bool PermanentlyUnavailable { get; init; }
}
