namespace Acta;

/// <summary>Validated sleep arm/consume under the attempt's fence; exactly one of the two due-instant inputs is set.</summary>
internal sealed record ArmOrConsumeJobSleepTimerCommand(ExecutionFence Fence, string Name, int? DelaySeconds, DateTime? ResumeAtUtc);
