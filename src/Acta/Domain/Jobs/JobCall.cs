namespace Acta;

/// <summary>
/// One job and the input to enqueue it with, as the manifest's generated factory returns it:
/// <c>jobs.EnqueueAsync(ShippingJobs.ShipOrder(order))</c>. <see cref="IJobs"/> enqueues, schedules, and runs a call
/// by the job it names, never by its input's type.
/// </summary>
public readonly record struct JobCall<TInput>(JobContract<TInput> Contract, TInput Input)
    where TInput : notnull;

/// <summary>
/// A <see cref="JobCall{TInput}"/> of a job that returns a result: its enqueue answers a <see cref="JobHandle{TResult}"/>
/// and its run-and-wait a <see cref="JobOutcome{TResult}"/>.
/// </summary>
public readonly record struct JobCall<TInput, TResult>(JobContract<TInput, TResult> Contract, TInput Input)
    where TInput : notnull
    where TResult : notnull;
