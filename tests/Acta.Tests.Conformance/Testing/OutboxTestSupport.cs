using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    public HookedOutboxStore(IServiceProvider services, OutboxSourceConnection inner) =>
        Source = services
            .GetRequiredService<IOutboxOperations>()
            .OpenSource(
                inner with
                {
                    Session = new HookedSession(
                        inner.Session,
                        stem => stem == "DeleteClaimedOutbox" && BeforeDelete is { } hook ? hook() : Task.CompletedTask
                    ),
                }
            );

    /// <summary>When set, awaited just before the real delete runs (models a crash after the target commit).</summary>
    public Func<Task>? BeforeDelete { get; set; }

    public IOutboxSource Source { get; }
}

/// <summary>
/// The real jobs operations with the owned batch enqueue over the ledger session, able to raise a chosen exception
/// instead of enqueuing, so a spec proves the relay's behavior when the target ingestion path fails (an infrastructure
/// failure that releases the claim, or a deterministic rejection that reschedules).
/// </summary>
internal sealed class HookedJobSubmission
{
    public HookedJobSubmission(IServiceProvider services)
    {
        var session = new HookedSession(
            services.GetRequiredService<IDbSession>(),
            stem => FailInstead is { } make && stem.StartsWith("Enqueue", StringComparison.Ordinal) ? throw make() : Task.CompletedTask
        );
        var dialect = services.GetRequiredService<ISqlDialect>();
        var wakeup = services.GetRequiredService<WorkerWakeupPublisher>();
        var options = services.GetRequiredService<IOptions<JobsOptions>>();
        var enqueueJob = new EnqueueJobHandler(session, dialect, wakeup, options);
        Jobs = ActivatorUtilities.CreateInstance<JobsOperations>(
            services,
            enqueueJob,
            new EnqueueJobsHandler(enqueueJob, session, dialect, wakeup, options)
        );
    }

    /// <summary>When set, invoked to obtain an exception to throw before the real enqueue runs.</summary>
    public Func<Exception>? FailInstead { get; set; }

    public IJobsOperations Jobs { get; }
}

/// <summary>Opens a provider's outbox source the way the relay registry does: its connection, opened by the outbox operations.</summary>
internal static class OutboxSourceOpening
{
    /// <summary>
    /// The outbox operations come from a provider graph built for the call and disposed at once, so a spec whose own
    /// services hold no ledger keeps none. The opened source holds only its own session and dialect.
    /// </summary>
    public static IOutboxSource OpenOutboxSource(this IConformanceFixture fixture, string schemaName, string table)
    {
        var services = new ServiceCollection();
        services.UseActa(j => fixture.ApplyProvider(j, schemaName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOutboxOperations>().OpenSource((OutboxSourceConnection)fixture.CreateOutboxConnection(table));
    }
}

/// <summary>A real session that awaits a hook, given the SQL stem, before each named command runs.</summary>
internal sealed class HookedSession(IDbSession inner, Func<string, Task> before) : IDbSession
{
    public DbProvider Provider => inner.Provider;

    public string Schema => inner.Schema;

    public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => inner.OpenConnectionAsync(ct);

    public async Task<T> QueryAsync<T>(
        string stem,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    )
    {
        await before(stem);
        return await inner.QueryAsync(stem, bind, read, ct);
    }

    public async Task<T> QueryAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, CancellationToken, Task<T>> read,
        CancellationToken ct
    )
    {
        await before(command.Operation);
        return await inner.QueryAsync(command, bind, read, ct);
    }

    public async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        await before(command.Operation);
        return await inner.ExecuteAsync(command, bind, mapRow, ct);
    }

    public async Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
        DbTransaction transaction,
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
    {
        await before(command.Operation);
        return await inner.ExecuteInTransactionAsync(transaction, command, bind, mapRow, ct);
    }

    public async Task<T?> ExecuteSingleAsync<T>(
        StoreCommand command,
        Action<DbCommand> bind,
        Func<DbDataReader, T> mapRow,
        CancellationToken ct
    )
        where T : class
    {
        await before(command.Operation);
        return await inner.ExecuteSingleAsync(command, bind, mapRow, ct);
    }

    public async Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct)
    {
        await before(command.Operation);
        await inner.ExecuteAsync(command, bind, ct);
    }

    public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        inner.RunWithRetryAsync(action, ct);

    public Task<T> RunInOwnedTransactionAsync<T>(
        Func<DbTransaction, CancellationToken, Task<(T Result, bool Commit)>> work,
        CancellationToken ct
    ) => inner.RunInOwnedTransactionAsync(work, ct);
}
