namespace Acta;

/// <summary>Mechanical byte-id capacity accounting for one closed persisted code family.</summary>
public sealed record CodeCapacityReport(
    int Assigned,
    int Deprecated,
    int Retired,
    int PermanentlyReserved,
    int HeldReserve,
    int Available,
    int InvalidSentinelValues
)
{
    public int Consumed => Assigned + Deprecated + Retired + PermanentlyReserved;
}
