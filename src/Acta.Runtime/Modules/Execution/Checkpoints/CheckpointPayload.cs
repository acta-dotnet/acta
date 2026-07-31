namespace Acta.Runtime.Modules.Execution.Checkpoints;

/// <summary>
/// Shared write-guard for variable checkpoint ops: a value must carry a real payload
/// (a non-<c>None</c> format with a non-zero id). Used by the <see cref="CheckpointSlot"/> set and
/// get-or-set shapes.
/// </summary>
internal static class CheckpointPayload
{
    public static void EnsureWritable(JobPayload payload)
    {
        if (payload.IsNone || payload.Format.Id == 0)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }
    }
}
