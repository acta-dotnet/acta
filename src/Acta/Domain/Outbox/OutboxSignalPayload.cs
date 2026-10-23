namespace Acta;

/// <summary>
/// One parked operator command, JSON-encoded into the signal checkpoint's value. <c>CommandId</c> is
/// minted per park, so the stored value names one command: admission, plan, and consume compare it to act
/// only on the command they read. A null <c>OutboxIds</c> targets every quarantined row of the source.
/// <c>PlannedIds</c> stays null until the applying tick records the quarantined rows the command targets,
/// before it changes any; a replay changes and reports those rows and no others.
/// </summary>
internal sealed record OutboxSignalPayload(
    Guid CommandId,
    string? Actor,
    string? ReasonMessage,
    IReadOnlyList<Guid>? OutboxIds,
    IReadOnlyList<Guid>? PlannedIds = null
);
