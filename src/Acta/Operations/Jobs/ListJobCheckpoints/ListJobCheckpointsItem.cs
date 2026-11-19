namespace Acta;

/// <summary>
/// One durable checkpoint of a job: a user variable, signal, sleep timer, progress, or child's outcome, told apart by its
/// kind. Its value is stated by format and size and read by <see cref="IJobs.GetCheckpointValueAsync"/>. User checkpoints
/// are named in dotted kebab, system ones <c>sys.*</c>.
/// </summary>
/// <param name="Kind">The checkpoint's kind: variable, signal, timer, progress, or child.</param>
/// <param name="Name">The checkpoint's name, unique within its job and kind.</param>
/// <param name="Status">Pending, set, or expired for a signal or a child, pending or consumed for a timer; null for a kind that
/// has none, such as a variable.</param>
/// <param name="DueAtUtc">When a bounded wait expires or a timer is due, in UTC; null otherwise.</param>
/// <param name="ValueFormatId">The id of the format the checkpoint's value is stored in: 1 for JSON, 2 for bytes, 128 to 255 for
/// an application's own; 0 when it holds no value.</param>
/// <param name="ValueByteLength">The stored value's size in bytes; null when it holds none. The value itself is read from
/// /jobs/{jobRef}/checkpoints/{kind}/{name}/value.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC; any write moves it.</param>
public sealed record ListJobCheckpointsItem(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    long? ValueByteLength,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
