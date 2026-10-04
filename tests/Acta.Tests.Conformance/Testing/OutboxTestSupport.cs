using System.Data.Common;
using Acta.Runtime;

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
    byte[]? Input,
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
/// The real provider source over a session that can inject a failure at a chosen source call (the crash
/// windows the brief proves) while every call still runs against the real provider database.
/// </summary>
internal sealed class HookedOutboxStore
{
    public HookedOutboxStore(OutboxSource inner) => Source = new OutboxSource(new HookedSession(inner.Session, this), inner.Dialect);

    /// <summary>When set, awaited just before the real delete runs (models a crash after the target commit).</summary>
    public Func<Task>? BeforeDelete { get; set; }

    public OutboxSource Source { get; }

    private sealed class HookedSession(IDbSession inner, HookedOutboxStore store) : IDbSession
    {
        public DbProvider Provider => inner.Provider;

        public string Schema => inner.Schema;

        public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => inner.OpenConnectionAsync(ct);

        public Task<T> QueryAsync<T>(
            string stem,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => inner.QueryAsync(stem, bind, read, ct);

        public Task<T> QueryAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => inner.QueryAsync(command, bind, read, ct);

        public Task<IReadOnlyList<T>> ExecuteAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => inner.ExecuteAsync(command, bind, mapRow, ct);

        public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
            DbTransaction transaction,
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => inner.ExecuteInTransactionAsync(transaction, command, bind, mapRow, ct);

        public Task<T?> ExecuteSingleAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        )
            where T : class => inner.ExecuteSingleAsync(command, bind, mapRow, ct);

        public async Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct)
        {
            if (command.Operation == "DeleteClaimedOutbox" && store.BeforeDelete is { } hook)
            {
                await hook();
            }
            await inner.ExecuteAsync(command, bind, ct);
        }

        public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            inner.RunWithRetryAsync(action, ct);

        public Task<T> RunInOwnedTransactionAsync<T>(
            Func<DbTransaction, CancellationToken, Task<(T Result, bool Commit)>> work,
            CancellationToken ct
        ) => inner.RunInOwnedTransactionAsync(work, ct);
    }
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
        return FailInstead is { } make ? throw make() : inner.EnqueueBatchAsync(requests, ct);
    }
}
