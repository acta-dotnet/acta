namespace Acta.Runtime;

/// <summary>
/// The one generic CRUD operation over a <c>checkpoints</c> slot (user variables and the progress
/// slot): last-writer-wins set, get, atomic get-or-set, exists, and delete, dispatched by
/// <see cref="CheckpointSlotAction"/> through <see cref="Execution.CheckpointSlotAsync"/>. Checkpoint
/// kinds with their own concurrency choreography (signals, timers, child latches) keep dedicated
/// operations; plain slot CRUD shares this one shape. Caller owns name validation, kind selection,
/// and typed serialization; this op owns the durable row shape.
/// </summary>
internal static class CheckpointSlot
{
    public static Task SetAsync(Execution store, long jobId, CheckpointKindCode kind, string name, JobPayload payload, CancellationToken ct)
    {
        CheckpointPayload.EnsureWritable(payload);
        return Run(store, CheckpointSlotAction.Set, jobId, kind, name, payload, ct);
    }

    public static async Task<CheckpointValue?> GetAsync(
        Execution store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    )
    {
        var row = await Run(store, CheckpointSlotAction.Get, jobId, kind, name, null, ct);
        return row.Found == 0 ? null : ToValue(row);
    }

    public static async Task<CheckpointValue> GetOrSetAsync(
        Execution store,
        long jobId,
        CheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        CheckpointPayload.EnsureWritable(payload);
        var row = await Run(store, CheckpointSlotAction.GetOrSet, jobId, kind, name, payload, ct);
        return row.Found == 0 ? throw new InvalidOperationException("checkpoint_slot get-or-set returned no stored value.") : ToValue(row);
    }

    public static async Task<bool> ExistsAsync(Execution store, long jobId, CheckpointKindCode kind, string name, CancellationToken ct) =>
        (await Run(store, CheckpointSlotAction.Exists, jobId, kind, name, null, ct)).Found != 0;

    public static async Task<bool> DeleteAsync(Execution store, long jobId, CheckpointKindCode kind, string name, CancellationToken ct) =>
        (await Run(store, CheckpointSlotAction.Delete, jobId, kind, name, null, ct)).Found != 0;

    private static Task<CheckpointSlotResponse> Run(
        Execution store,
        CheckpointSlotAction action,
        long jobId,
        CheckpointKindCode kind,
        string name,
        JobPayload? payload,
        CancellationToken ct
    ) =>
        store.CheckpointSlotAsync(
            new CheckpointSlotCommand(action, jobId, kind, name, payload?.Format.Id ?? 0, payload?.Data.ToArray()),
            ct
        );

    private static CheckpointValue ToValue(CheckpointSlotResponse row)
    {
        return row.ValueFormatId is not { } format || row.Value is not { } value || row.Version is not { } version
            ? throw new InvalidOperationException("checkpoint_slot returned a found slot with NULL value columns.")
            : new CheckpointValue(format, value, version);
    }
}
