namespace Acta.Runtime;

/// <summary>The namespace's <c>sys.recovery</c> slot to check, by its job id.</summary>
internal sealed record RepairRecoverySlotCommand(int NamespaceId, long JobId);
