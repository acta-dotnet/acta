using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// <c>IActaBuilder.DoNotRetry</c>: a declared type, or one derived from it, ends a job where Acta's built-in non-retryable
/// list is read, in the worker's completion and in <c>ctx.EndsJob</c> alike, and nowhere earlier.
/// </summary>
public sealed class DoNotRetryTests
{
    private const string JobName = "orders.charge";

    [Fact]
    public void A_declared_type_and_its_derived_types_match_and_no_other_type_does()
    {
        var declared = new NonRetryableExceptions([typeof(ArgumentException)]);

        Assert.True(declared.Matches(new ArgumentException("bad"), JobName, attemptCancelled: false));
        Assert.True(declared.Matches(new ArgumentNullException("orderId"), JobName, attemptCancelled: false));
        Assert.False(declared.Matches(new InvalidOperationException("busy"), JobName, attemptCancelled: false));
    }

    [Fact]
    public void Only_the_thrown_exception_is_matched_never_one_it_wraps()
    {
        var declared = new NonRetryableExceptions([typeof(ArgumentException)]);

        Assert.False(
            declared.Matches(new InvalidOperationException("wrapped", new ArgumentException("bad")), JobName, attemptCancelled: false)
        );
        Assert.False(declared.Matches(new AggregateException(new ArgumentException("bad")), JobName, attemptCancelled: false));
    }

    [Fact]
    public void The_built_in_list_holds_with_or_without_declarations()
    {
        Assert.True(new NonRetryableExceptions([]).Matches(new NotSupportedException(), JobName, attemptCancelled: false));
        Assert.True(
            new NonRetryableExceptions([typeof(ArgumentException)]).Matches(new NotImplementedException(), JobName, attemptCancelled: false)
        );
        Assert.False(new NonRetryableExceptions([]).Matches(new ArgumentException("bad"), JobName, attemptCancelled: false));
    }

    [Fact]
    public void A_system_job_reads_the_built_in_list_only()
    {
        var declared = new NonRetryableExceptions([typeof(Exception)]);

        Assert.False(declared.Matches(new ArgumentException("bad"), "sys.retention", attemptCancelled: false));
        Assert.True(declared.Matches(new NotSupportedException(), "sys.retention", attemptCancelled: false));
    }

    [Fact]
    public void Declaring_a_type_twice_is_a_no_op()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.DoNotRetry<ArgumentException>().DoNotRetry<ArgumentException>();

        Assert.Equal(typeof(ArgumentException), Assert.Single(builder.DoNotRetryTypes));
    }

    [Fact]
    public async Task A_declared_type_lands_terminal_failed_as_a_built_in_one_does()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10, doNotRetry: [typeof(ArgumentException)]);

        var outcome = await harness.RunAsync(static (_, _) => throw new ArgumentNullException("orderId"));

        Assert.Equal(RunOnceStatus.Failed, outcome);
        Assert.Equal(EventReason.NonRetryableException, harness.Completion.Reason);
        Assert.Equal((byte)JobStatusCode.Failed, harness.Completion.HandlerStatusCode);
        Assert.Null(harness.Completion.RescheduleEventCode);
    }

    [Fact]
    public async Task A_type_nobody_declared_is_still_retried()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10, doNotRetry: [typeof(ArgumentException)]);

        var outcome = await harness.RunAsync(static (_, _) => throw new InvalidOperationException("busy"));

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.Equal(EventReason.UnhandledException, harness.Completion.Reason);
        Assert.Null(harness.Completion.HandlerStatusCode);
        Assert.NotNull(harness.Completion.RescheduleEventCode);
    }

    [Fact]
    public async Task A_handler_that_wraps_a_declared_type_is_still_retried()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10, doNotRetry: [typeof(ArgumentException)]);

        var outcome = await harness.RunAsync(
            static (_, _) => throw new InvalidOperationException("lookup failed", new ArgumentException("bad id"))
        );

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.Equal(EventReason.UnhandledException, harness.Completion.Reason);
        Assert.Null(harness.Completion.HandlerStatusCode);
    }

    [Fact]
    public async Task A_system_job_retries_a_declared_type_and_its_context_agrees()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10, jobName: "sys.retention", doNotRetry: [typeof(Exception)]);
        var endsJob = new List<bool>();

        var outcome = await harness.RunAsync(
            (ctx, _) =>
            {
                endsJob.Add(ctx.EndsJob(new ArgumentException("bad")));
                throw new ArgumentException("bad");
            }
        );

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.Equal(EventReason.UnhandledException, harness.Completion.Reason);
        Assert.Equal([false], endsJob);
    }

    [Fact]
    public async Task Declaring_Exception_leaves_an_execution_timeout_retryable()
    {
        var harness = new WorkerExecutorHarness(doNotRetry: [typeof(Exception)]);

        var outcome = await harness.RunAsync(
            async (_, ct) =>
            {
                harness.TimeOutAttempt();
                await Task.Delay(Timeout.Infinite, ct);
            }
        );

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.Equal(EventReason.ExecutionTimeout, harness.Completion.Reason);
        Assert.Null(harness.Completion.HandlerStatusCode);
    }

    [Fact]
    public void A_declared_type_thrown_by_a_cancelled_attempt_is_left_to_the_budget_and_a_built_in_one_is_not()
    {
        var declared = new NonRetryableExceptions([typeof(Exception)]);

        Assert.False(declared.Matches(new ObjectDisposedException("socket"), JobName, attemptCancelled: true));
        Assert.True(declared.Matches(new NotSupportedException(), JobName, attemptCancelled: true));
    }

    [Fact]
    public async Task A_cancelled_attempt_that_surfaces_as_a_declared_type_is_still_retried()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10, doNotRetry: [typeof(Exception)]);
        var endsJob = new List<bool>();

        var outcome = await harness.RunAsync(
            (ctx, _) =>
            {
                harness.AbortAttempt();
                var disposed = new ObjectDisposedException("socket");
                endsJob.Add(ctx.EndsJob(disposed));
                throw disposed;
            }
        );

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        Assert.Equal(EventReason.UnhandledException, harness.Completion.Reason);
        Assert.Null(harness.Completion.HandlerStatusCode);
        Assert.Equal([false], endsJob);
    }

    [Fact]
    public async Task The_attempt_context_ends_the_job_on_a_declared_type_and_never_on_what_the_worker_decides_first()
    {
        var harness = new WorkerExecutorHarness(doNotRetry: [typeof(Exception)]);
        var endsJob = new List<bool>();

        var outcome = await harness.RunAsync(
            (ctx, _) =>
            {
                endsJob.Add(ctx.EndsJob(new ArgumentException("bad")));
                endsJob.Add(ctx.EndsJob(new RescheduleJobException(TimeSpan.FromMinutes(1))));
                endsJob.Add(ctx.EndsJob(new ExecutionFencedException("fenced")));
                endsJob.Add(ctx.EndsJob(new StepOwnershipLostException("charge")));
                return Task.CompletedTask;
            }
        );

        Assert.Equal(RunOnceStatus.Completed, outcome);
        Assert.Equal([true, false, false, false], endsJob);
    }

    [Fact]
    public async Task Without_a_declaration_the_attempt_context_leaves_the_type_to_the_retry_budget()
    {
        var harness = new WorkerExecutorHarness();
        var endsJob = new List<bool>();

        var outcome = await harness.RunAsync(
            (ctx, _) =>
            {
                endsJob.Add(ctx.EndsJob(new ArgumentException("bad")));
                return Task.CompletedTask;
            }
        );

        Assert.Equal(RunOnceStatus.Completed, outcome);
        Assert.Equal([false], endsJob);
    }
}
