namespace Acta;

/// <summary>
/// One durable <c>checkpoints</c> slot of a job: a user variable, signal, sleep timer, progress slot, or child
/// latch, told apart by <see cref="Kind"/>. <see cref="Status"/> is Pending or Set for a signal or child latch,
/// Pending or Consumed for a timer, and null for the stateless kinds. <see cref="DueAtUtc"/> is a timer's due
/// instant, <see cref="Value"/> is null when the slot carries no payload, and <see cref="ModifiedAtUtc"/> moves on
/// any write. User slots are named in dotted kebab, system slots <c>sys.*</c>.
/// </summary>
public sealed record CheckpointItem(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    JobPayload? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
