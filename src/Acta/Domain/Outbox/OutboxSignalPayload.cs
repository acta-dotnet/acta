namespace Acta;

/// <summary>
/// One parked operator command, JSON-encoded into the signal checkpoint's value. <c>CommandId</c> is
/// minted per park, so the stored value names one command: admission compares it to tell "my write landed"
/// from "a concurrent command landed", and consume compares it to delete only the command the tick read.
/// A null <c>OutboxIds</c> targets every quarantined row of the source.
/// </summary>
internal sealed record OutboxSignalPayload(Guid CommandId, string? Actor, string? ReasonMessage, IReadOnlyList<Guid>? OutboxIds);
