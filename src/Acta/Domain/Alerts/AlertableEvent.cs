namespace Acta;

/// <summary>
/// One alert-relevant <c>events</c> row for the <c>sys.alerts</c> generate phase, joined to its definition's alert
/// policy and classified from its immutable code and details' <c>reason</c>, never from <c>jobs_state</c>, so
/// projection is a pure function of the event stream. <see cref="CreatedAtUtc"/> positions the cursor and skips, walking
/// by (stamp, id) because id order can invert against commit order. <see cref="Superseded"/> marks a replayed skip a
/// newer success of its job already answered.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record AlertableEvent(
    long EventId,
    DateTime CreatedAtUtc,
    long JobId,
    int DefinitionId,
    string JobName,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    EventCode EventCode,
    string? EventMessage,
    byte DetailsFormatId,
    byte[]? Details,
    bool Superseded
)
{
    /// <summary>The <see cref="EventReason"/> kebab from the details payload, or null when it records none.</summary>
    public string? Reason() => EventDetails.Read(DetailsFormatId, Details, "reason");
}
