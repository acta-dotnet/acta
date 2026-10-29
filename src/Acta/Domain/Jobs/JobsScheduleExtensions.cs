using System.Data.Common;

namespace Acta;

/// <summary>
/// <c>ScheduleAsync</c>: enqueue with the first run's time as an argument, one job whose earliest claim waits for it; a
/// recurring schedule is <c>[Schedule]</c>. A delay counts on the database clock, rounded up to whole seconds, and the
/// time is applied after <c>configure</c>, so it wins over a <c>Delayed</c> or <c>NextRunAt</c> set there.
/// </summary>
public static class JobsScheduleExtensions
{
    /// <summary>
    /// Typed enqueue whose first run waits <paramref name="delay"/> (database clock, whole seconds rounded up). Creates
    /// one job, not a recurring schedule.
    /// </summary>
    public static ValueTask<JobHandle> ScheduleAsync<TInput>(
        this IJobs jobs,
        TInput input,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(input, After(delay, configure), ct);

    /// <summary>
    /// Typed enqueue whose first run is at <paramref name="runAt"/>. Creates one job, not a recurring schedule. A
    /// <see cref="DateTime"/> converts as it does to any <see cref="DateTimeOffset"/>: an unspecified kind reads as local time.
    /// </summary>
    public static ValueTask<JobHandle> ScheduleAsync<TInput>(
        this IJobs jobs,
        TInput input,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(input, At(runAt, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<JobHandle> ScheduleAsync<TInput>(
        this IJobs jobs,
        JobCall<TInput> job,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(job, After(delay, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, TInput, DateTimeOffset, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<JobHandle> ScheduleAsync<TInput>(
        this IJobs jobs,
        JobCall<TInput> job,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(job, At(runAt, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<JobHandle<TResult>> ScheduleAsync<TInput, TResult>(
        this IJobs jobs,
        JobCall<TInput, TResult> job,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => Required(jobs).EnqueueAsync(job, After(delay, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, TInput, DateTimeOffset, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<JobHandle<TResult>> ScheduleAsync<TInput, TResult>(
        this IJobs jobs,
        JobCall<TInput, TResult> job,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => Required(jobs).EnqueueAsync(job, At(runAt, configure), ct);

    /// <summary>Transactional twin of the delayed typed schedule; the transactional enqueue rules apply.</summary>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput>(
        this IJobs jobs,
        DbTransaction transaction,
        TInput input,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(transaction, input, After(delay, configure), ct);

    /// <summary>Transactional twin of the timed typed schedule; the transactional enqueue rules apply.</summary>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput>(
        this IJobs jobs,
        DbTransaction transaction,
        TInput input,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(transaction, input, At(runAt, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, DbTransaction, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput>(
        this IJobs jobs,
        DbTransaction transaction,
        JobCall<TInput> job,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(transaction, job, After(delay, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, DbTransaction, TInput, DateTimeOffset, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput>(
        this IJobs jobs,
        DbTransaction transaction,
        JobCall<TInput> job,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => Required(jobs).EnqueueAsync(transaction, job, At(runAt, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, DbTransaction, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput, TResult>(
        this IJobs jobs,
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        TimeSpan delay,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => Required(jobs).EnqueueAsync(transaction, job, After(delay, configure), ct);

    /// <inheritdoc cref="ScheduleAsync{TInput}(IJobs, DbTransaction, TInput, DateTimeOffset, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public static ValueTask<EnqueueJobResponse> ScheduleAsync<TInput, TResult>(
        this IJobs jobs,
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        DateTimeOffset runAt,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => Required(jobs).EnqueueAsync(transaction, job, At(runAt, configure), ct);

    private static IJobs Required(IJobs jobs) => jobs ?? throw new ArgumentNullException(nameof(jobs));

    private static Action<JobEnqueueOptionsBuilder> After(TimeSpan delay, Action<JobEnqueueOptionsBuilder>? configure) =>
        builder =>
        {
            configure?.Invoke(builder);
            builder.Delayed(delay);
        };

    private static Action<JobEnqueueOptionsBuilder> At(DateTimeOffset runAt, Action<JobEnqueueOptionsBuilder>? configure) =>
        builder =>
        {
            configure?.Invoke(builder);
            builder.NextRunAt(runAt);
        };
}
