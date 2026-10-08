namespace Acta;

/// <summary>Transient command selectors, not persisted codes. Order is the retention sweep order.</summary>
internal enum RetentionSection
{
    Jobs = 1,
    Events = 2,
    SettledAlerts = 3,
    UndeliveredAlerts = 4,
    PoisonSkipCheckpoints = 5,
    Workers = 6,
    Locks = 7,
    Lanes = 8,
}
