namespace Acta.Runtime;

/// <summary>Token-CAS reschedule of recoverable failures back to Pending with backoff.</summary>
internal sealed record RescheduleOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxReschedule> Rows);
