using Acta.Modules.Execution.Jobs;
using Acta.Modules.Outbox;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// One producer-staged external-outbox row a spec seeds directly (the relay never inserts; producers
/// do). Mirrors the canonical columns the claim projects and the relay finalizes under.
/// </summary>
public readonly record struct OutboxSeed(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    byte InputFormatId,
    byte[]? InputData,
    string DeduplicationKey,
    byte? PriorityCode,
    DateTime CreatedAtUtc,
    DateTime NextAttemptAtUtc,
    byte StatusCode,
    int FailureCount,
    Guid? ClaimToken = null,
    DateTime? ClaimUntilUtc = null,
    string? Meta = null
);

/// <summary>The relay-visible state of one source row, read back for assertions.</summary>
public readonly record struct OutboxRowState(
    bool Exists,
    byte StatusCode,
    int FailureCount,
    Guid? ClaimToken,
    DateTime? ClaimUntilUtc,
    DateTime NextAttemptAtUtc,
    string? LastError
);

/// <summary>
/// A delegating <see cref="IOutboxRelayStore"/> decorator with optional hooks, so a handoff spec can
/// inject a failure at a chosen source seam (the crash windows the brief proves) while every other call
/// passes straight through to the real provider store.
/// </summary>
internal sealed class HookedOutboxStore(IOutboxRelayStore inner) : IOutboxRelayStore
{
    /// <summary>When set, awaited just before the real delete runs (models a crash after the target commit).</summary>
    public Func<Task>? BeforeDelete { get; set; }

    public Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct) =>
        inner.ClaimDueAsync(command, ct);

    public async Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct)
    {
        if (BeforeDelete is { } hook)
        {
            await hook();
        }
        await inner.DeleteClaimedAsync(command, ct);
    }

    public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) => inner.RescheduleAsync(command, ct);

    public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) => inner.QuarantineAsync(command, ct);

    public Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) => inner.ReleaseClaimedAsync(command, ct);

    public Task<long> CountBacklogAsync(CancellationToken ct) => inner.CountBacklogAsync(ct);
}

/// <summary>
/// A delegating <see cref="IJobSubmission"/> that can raise a chosen exception instead of enqueuing, so a
/// spec proves the relay's behavior when the target ingestion path fails (an infrastructure failure that
/// releases the claim, or a deterministic rejection that reschedules).
/// </summary>
internal sealed class HookedJobSubmission(IJobSubmission inner) : IJobSubmission
{
    /// <summary>When set, invoked to obtain an exception to throw before the real enqueue runs.</summary>
    public Func<Exception>? FailInstead { get; set; }

    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct)
    {
        if (FailInstead is { } make)
        {
            throw make();
        }
        return inner.EnqueueBatchAsync(requests, ct);
    }
}
