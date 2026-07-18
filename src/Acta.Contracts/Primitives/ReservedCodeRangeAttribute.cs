namespace Acta;

/// <summary>
/// Holds a byte-id range outside normal allocation. Held ranges are reported separately from
/// consumed capacity; set <see cref="PermanentlyUnavailable"/> when every id in the range is
/// permanently consumed and can never be assigned.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class ReservedCodeRangeAttribute : Attribute
{
    public ReservedCodeRangeAttribute(byte start, byte end, string reason)
    {
        Start = start;
        End = end;
        Reason = reason;
    }

    public byte Start { get; }

    public byte End { get; }

    public string Reason { get; }

    public bool PermanentlyUnavailable { get; init; }
}
