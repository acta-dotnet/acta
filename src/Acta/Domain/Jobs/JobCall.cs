namespace Acta;

/// <summary>
/// One job and the input to enqueue it with, as the manifest's generated factory returns it:
/// <c>jobs.EnqueueAsync(ShippingJobs.ShipOrder(order))</c>. <see cref="IJobs"/> enqueues, schedules, and calls it
/// by the job it names, never by its input's type, also when it is passed where an input is expected.
/// </summary>
public readonly record struct JobCall<TInput>(JobContract<TInput> Contract, TInput Input) : IJobCall
    where TInput : notnull
{
    T IJobCall.Accept<T>(IJobCallVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>
/// A <see cref="JobCall{TInput}"/> of a job that returns a result: its enqueue answers a <see cref="JobHandle{TResult}"/>
/// and its call a <see cref="JobOutcome{TResult}"/>.
/// </summary>
public readonly record struct JobCall<TInput, TResult>(JobContract<TInput, TResult> Contract, TInput Input) : IJobCall
    where TInput : notnull
    where TResult : notnull
{
    T IJobCall.Accept<T>(IJobCallVisitor<T> visitor) => visitor.Visit(this);
}

/// <summary>A generated call that hands itself, with its static types, to whatever routes it.</summary>
internal interface IJobCall
{
    T Accept<T>(IJobCallVisitor<T> visitor);
}

/// <summary>Routes a generated call by its static types, so no reflection reaches it.</summary>
internal interface IJobCallVisitor<out T>
{
    T Visit<TInput>(JobCall<TInput> call)
        where TInput : notnull;

    T Visit<TInput, TResult>(JobCall<TInput, TResult> call)
        where TInput : notnull
        where TResult : notnull;
}
