namespace Acta.Runtime;

/// <summary>Validated sleep arm/consume; exactly one of the two due-instant inputs is set.</summary>
internal sealed record ArmOrConsumeSleepTimerCommand(long JobId, string Name, int? DelaySeconds, DateTime? ResumeAtUtc);
