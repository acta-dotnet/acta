namespace Acta;

/// <summary>
/// One parked operator command, JSON-encoded into the signal checkpoint's value. <c>CommandId</c> is
/// minted per park; its uniqueness is what lets the admission statement distinguish "my write landed"
/// from "a concurrent command landed" by comparing the stored value. A null <c>OutboxIds</c> targets
/// every quarantined row of the source.
/// </summary>
internal sealed record OutboxSignalPayload(Guid CommandId, string? Actor, string? ReasonMessage, IReadOnlyList<Guid>? OutboxIds);
