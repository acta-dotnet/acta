namespace Acta;

/// <summary>
/// One durable checkpoint of a job: a user variable, signal, sleep timer, progress, or child
/// latch, told apart by <see cref="Kind"/>. <see cref="Status"/> is Pending or Set for a signal or child latch,
/// Pending or Consumed for a timer, and null for the stateless kinds. <see cref="DueAtUtc"/> is a timer's due
/// instant, <see cref="Value"/> is null when the checkpoint carries no payload, and <see cref="ModifiedAtUtc"/> moves on
/// any write. User checkpoints are named in dotted kebab, system ones <c>sys.*</c>.
/// </summary>
public sealed record ListJobCheckpointsItem(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    JobPayload? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
