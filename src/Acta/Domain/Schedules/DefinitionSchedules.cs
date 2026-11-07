namespace Acta;

/// <summary>
/// All schedules for one definition, grouped for a single <c>RegisterSchedules</c> call: the slot's serialized
/// default input, the delivery that decides whether a slot exists, and each schedule as the registration plan
/// computed it.
/// </summary>
internal sealed record DefinitionSchedules(
    int NamespaceId,
    int DefinitionId,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    ScheduledDeliveryCode ScheduledDelivery,
    IReadOnlyList<SlotSchedule> Schedules
);
