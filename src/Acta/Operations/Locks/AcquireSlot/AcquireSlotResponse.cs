namespace Acta;

/// <summary>The slot's hold, its token carrying the slot's own key; null when every slot is held or a racer took it.</summary>
internal readonly record struct AcquireSlotResponse(LockToken? Token);
