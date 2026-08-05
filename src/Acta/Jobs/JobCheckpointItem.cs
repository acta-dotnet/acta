namespace Acta;

/// <summary>
/// One durable <c>checkpoints</c> slot for the job read surface: a user variable, signal, sleep timer,
/// progress slot, or child latch, discriminated by <see cref="Kind"/>. Carries exactly the columns the
/// merged slot table stores; <see cref="Value"/> is <c>null</c> for the payload-free kinds and states.
/// </summary>
/// <param name="Kind">Which substrate feature owns the slot (variable / signal / timer / progress / child-latch).</param>
/// <param name="Name">Slot name; dotted-kebab for user slots, <c>sys.*</c> for system slots.</param>
/// <param name="Status">Pending/Set (signals, child latches) or Pending/Consumed (timers); <c>null</c> for the stateless kinds.</param>
/// <param name="DueAtUtc">The named wait's due instant for timer slots; <c>null</c> for every other kind.</param>
/// <param name="Value">The decoded slot payload, or <c>null</c> when the slot carries none.</param>
/// <param name="CreatedAtUtc">First-write instant.</param>
/// <param name="ModifiedAtUtc">Last write of any kind (state transition or value overwrite).</param>
public sealed record JobCheckpointItem(
    JobCheckpointKindCode Kind,
    string Name,
    JobCheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    JobPayload? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);
