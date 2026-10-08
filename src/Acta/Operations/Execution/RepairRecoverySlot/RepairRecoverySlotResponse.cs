namespace Acta;

/// <summary>What the recovery-slot repair found: the row absent, in no need of repair, or re-armed by this call.</summary>
internal enum RecoverySlotRepair
{
    Missing = 0,
    Healthy = 1,
    Repaired = 2,
}

/// <summary>The recovery slot's state as the repair found or left it.</summary>
internal readonly record struct RepairRecoverySlotResponse(RecoverySlotRepair Repair);
