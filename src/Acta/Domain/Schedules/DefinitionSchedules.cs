namespace Acta;

/// <summary>
/// All schedules for one definition, grouped for a single <c>register_schedules</c> call. Carries
/// the slot's serialized default input, denormalized event recording, target status, and the slot cursor
/// (<c>SlotMinNextRunAtUtc</c> is MIN over live schedules; null means Paused, exhausted, or removed).
/// </summary>
internal sealed record DefinitionSchedules(
    int NamespaceId,
    int DefinitionId,
    string JobName,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    EventRecordingCode EventRecording,
    JobStatusCode SlotStatus,
    DateTime? SlotMinNextRunAtUtc,
    IReadOnlyList<SlotSchedule> Schedules
);
