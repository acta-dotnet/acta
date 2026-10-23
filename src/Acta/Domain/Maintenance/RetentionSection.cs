namespace Acta;

/// <summary>Transient command selectors, not persisted codes. Order is the retention sweep order.</summary>
internal enum RetentionSection
{
    Jobs = 1,
    Events = 2,
    SettledAlerts = 3,
    UndeliveredAlerts = 4,
    Workers = 5,
    Locks = 6,
    Lanes = 7,
}
