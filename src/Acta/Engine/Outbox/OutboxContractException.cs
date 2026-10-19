namespace Acta;

/// <summary>
/// A deterministic, non-recoverable defect in one external-outbox row (malformed <c>meta</c>, missing
/// tag name, or a payload above the target's hard inline cap). The relay quarantines the row
/// immediately rather than accruing retries.
/// </summary>
internal sealed class OutboxContractException(string message) : Exception(message);
