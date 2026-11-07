namespace Acta;

/// <summary>
/// The slot's hold, its token carrying the slot's own key; null when none was taken. LostRace says a slot was free
/// and a racer took it first, so another slot may still be free; without it every slot was held.
/// </summary>
internal readonly record struct AcquireLockSlotResponse(LockToken? Token, bool LostRace);
