using Acta.Payloads;

namespace Acta.Features.Execution.Checkpoints;

/// <summary>
/// Which CRUD shape a <c>checkpoint_slot</c> call takes. The values are the routine's dispatch
/// discriminator; they are never persisted.
/// </summary>
internal enum CheckpointSlotAction : short
{
    Set = 10,
    Get = 20,
    GetOrSet = 30,
    Exists = 40,
    Delete = 50,
}

/// <summary>
/// One row of <c>checkpoint_slot</c>: the action outcome flag plus the slot value columns (NULL for
/// the write/exists/delete shapes and for a missing slot).
/// </summary>
internal sealed record CheckpointSlotRow(int Found, byte? ValueFormatId, byte[]? Value, int? Version);

/// <summary>
/// The one generic CRUD operation over a <c>checkpoints</c> slot (user variables and the progress
/// slot): last-writer-wins set, get, atomic get-or-set, exists, and delete, dispatched by
/// <see cref="CheckpointSlotAction"/> through the store port's single slot call. Checkpoint
/// kinds with their own concurrency choreography (signals, timers, child latches) keep dedicated
/// operations; plain slot CRUD shares this one shape. Caller owns name validation, kind selection,
/// and typed serialization; this op owns the durable row shape.
/// </summary>
internal static class CheckpointSlot
{
    public static Task SetAsync(
        IExecutionStore store,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        CheckpointPayload.EnsureWritable(payload);
        return Run(store, CheckpointSlotAction.Set, jobId, kind, name, payload, ct);
    }

    public static async Task<CheckpointValue?> GetAsync(
        IExecutionStore store,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        CancellationToken ct
    )
    {
        var row = await Run(store, CheckpointSlotAction.Get, jobId, kind, name, null, ct);
        return row.Found == 0 ? null : ToValue(row);
    }

    public static async Task<CheckpointValue> GetOrSetAsync(
        IExecutionStore store,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        CheckpointPayload.EnsureWritable(payload);
        var row = await Run(store, CheckpointSlotAction.GetOrSet, jobId, kind, name, payload, ct);
        if (row.Found == 0)
        {
            throw new InvalidOperationException("checkpoint_slot get-or-set returned no stored value.");
        }

        return ToValue(row);
    }

    public static async Task<bool> ExistsAsync(
        IExecutionStore store,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, CheckpointSlotAction.Exists, jobId, kind, name, null, ct)).Found != 0;

    public static async Task<bool> DeleteAsync(
        IExecutionStore store,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, CheckpointSlotAction.Delete, jobId, kind, name, null, ct)).Found != 0;

    private static Task<CheckpointSlotRow> Run(
        IExecutionStore store,
        CheckpointSlotAction action,
        long jobId,
        JobCheckpointKindCode kind,
        string name,
        JobPayload? payload,
        CancellationToken ct
    ) =>
        store.CheckpointSlotAsync(
            new CheckpointSlotCommand(action, jobId, kind, name, payload?.Format.Id ?? 0, payload?.Data.ToArray()),
            ct
        );

    private static CheckpointValue ToValue(CheckpointSlotRow row)
    {
        if (row.ValueFormatId is not { } format || row.Value is not { } value || row.Version is not { } version)
        {
            throw new InvalidOperationException("checkpoint_slot returned a found slot with NULL value columns.");
        }

        return new CheckpointValue(format, value, version);
    }
}
