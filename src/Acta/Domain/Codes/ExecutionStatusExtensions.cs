namespace Acta;

public static partial class ExecutionStatusExtensions
{
    extension(ExecutionStatusCode value)
    {
        public bool IsTerminal =>
            value
                is ExecutionStatusCode.Succeeded
                    or ExecutionStatusCode.Rescheduled
                    or ExecutionStatusCode.Suspended
                    or ExecutionStatusCode.Paused
                    or ExecutionStatusCode.Failed
                    or ExecutionStatusCode.Cancelled
                    or ExecutionStatusCode.Orphaned;

        public ExecutionBehavior GetBehavior() =>
            value switch
            {
                ExecutionStatusCode.Succeeded => ExecutionBehavior.Succeeded,
                ExecutionStatusCode.Rescheduled or ExecutionStatusCode.Suspended or ExecutionStatusCode.Paused =>
                    ExecutionBehavior.Controlled,
                ExecutionStatusCode.Failed => ExecutionBehavior.Failed,
                ExecutionStatusCode.Cancelled => ExecutionBehavior.Cancelled,
                ExecutionStatusCode.Orphaned or ExecutionStatusCode.Unspecified => ExecutionBehavior.Indeterminate,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
            };
    }
}

/// <summary>Explicit runtime behavior of a completed execution attempt.</summary>
public enum ExecutionBehavior : byte
{
    Succeeded = 2,
    Controlled = 3,
    Failed = 4,
    Cancelled = 5,
    Indeterminate = 6,
}
