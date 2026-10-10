namespace Acta;

/// <summary>
/// The one generic CRUD operation over a <c>checkpoints</c> slot (user variables and the progress
/// slot): last-writer-wins set, get, atomic get-or-set, exists, and delete, dispatched by
/// <see cref="AccessCheckpointAction"/> through <see cref="ExecutionOperations.AccessCheckpointAsync"/>. Checkpoint
/// kinds with their own concurrency choreography (signals, timers, child latches) keep dedicated
/// operations; plain slot CRUD shares this one shape. Caller owns name validation, kind selection,
/// and typed serialization; this op owns the durable row shape.
/// </summary>
internal static class CheckpointSlot
{
    public static Task SetAsync(
        ExecutionOperations store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        EnsureWritable(payload);
        return Run(store, AccessCheckpointAction.Set, jobId, kind, name, payload, ct);
    }

    public static async Task<CheckpointValue?> GetAsync(
        ExecutionOperations store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    )
    {
        var row = await Run(store, AccessCheckpointAction.Get, jobId, kind, name, null, ct);
        return row.Found == 0 ? null : ToValue(row);
    }

    public static async Task<CheckpointValue> GetOrSetAsync(
        ExecutionOperations store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        EnsureWritable(payload);
        var row = await Run(store, AccessCheckpointAction.GetOrSet, jobId, kind, name, payload, ct);
        return row.Found == 0
            ? throw new InvalidOperationException("access_checkpoint get-or-set returned no stored value.")
            : ToValue(row);
    }

    public static async Task<bool> ExistsAsync(
        ExecutionOperations store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, AccessCheckpointAction.Exists, jobId, kind, name, null, ct)).Found != 0;

    public static async Task<bool> DeleteAsync(
        ExecutionOperations store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, AccessCheckpointAction.Delete, jobId, kind, name, null, ct)).Found != 0;

    // A variable value must carry a real payload: a non-None format with a non-zero id.
    private static void EnsureWritable(JobPayload payload)
    {
        if (payload.IsNone || payload.Format.Id == 0)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }
    }

    private static Task<AccessCheckpointResponse> Run(
        ExecutionOperations store,
        AccessCheckpointAction action,
        long jobId,
        CheckpointKindCode kind,
        string name,
        JobPayload? payload,
        CancellationToken ct
    ) =>
        store.AccessCheckpointAsync(
            new AccessCheckpointCommand(action, jobId, kind, name, payload?.Format.Id ?? 0, payload?.Data.ToArray()),
            ct
        );

    private static CheckpointValue ToValue(AccessCheckpointResponse row)
    {
        return row.ValueFormatId is not { } format || row.Value is not { } value || row.Version is not { } version
            ? throw new InvalidOperationException("access_checkpoint returned a found slot with NULL value columns.")
            : new CheckpointValue(format, value, version);
    }
}
