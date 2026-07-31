using Xunit;

namespace Acta.Tests.Context;

public class JobContextDeadlineTests
{
    private sealed class FakeCtx(DateTime? deadline) : JobContext
    {
        public override long JobId => 1;
        public override string JobNamespace => "ns";
        public override short NamespaceId => 1;
        public override string JobName => "j";
        public override CancellationToken CancellationToken => default;
        public override DateTime? DeadlineAtUtc { get; } = deadline;

        protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct) => Task.CompletedTask;

        protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct) => throw new NotSupportedException();

        protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
            where T : default => throw new NotSupportedException();

        protected override Task<T> GetOrSetVariableCoreAsync<T>(
            string name,
            Func<CancellationToken, Task<T>> valueFactory,
            CancellationToken ct
        ) => throw new NotSupportedException();

        protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) => throw new NotSupportedException();

        protected override Task ResetStateCoreAsync(CancellationToken ct) => throw new NotSupportedException();

        protected override Task SleepCoreAsync(string name, TimeSpan? delay, DateTime? resumeAtUtc, string? reason, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
            where T : default => throw new NotSupportedException();

        protected override Task<JobEnqueueOutcome> StartChildCoreAsync<TInput>(
            TInput input,
            JobEnqueueOptions options,
            CancellationToken ct
        ) => throw new NotSupportedException();

        protected override Task<JobEnqueueOutcome> StartChildCoreAsync(JobEnqueueRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct)
            where TResult : default => throw new NotSupportedException();

        protected override Task RunStepCoreAsync(
            string name,
            Func<CancellationToken, Task> body,
            StepOptions options,
            CancellationToken ct
        ) => throw new NotSupportedException();

        protected override Task<TResult> RunStepCoreAsync<TResult>(
            string name,
            Func<CancellationToken, Task<TResult>> body,
            StepOptions options,
            CancellationToken ct
        ) => throw new NotSupportedException();

        protected override Task<int?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task ReleaseLockCoreAsync(string key, LockScope scope, int version, CancellationToken ct) =>
            throw new NotSupportedException();

        protected override Task RaiseAlertCoreAsync(
            AlertSeverityCode severityCode,
            string title,
            string message,
            string? channelName,
            string? deduplicationKey,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    [Fact]
    public void No_deadline_is_never_overdue()
    {
        var ctx = new FakeCtx(null);
        Assert.False(ctx.IsOverdue);
        Assert.Null(ctx.TimeUntilDeadline);
    }

    [Fact]
    public void Past_deadline_is_overdue()
    {
        var ctx = new FakeCtx(DateTime.UtcNow.AddMinutes(-1));
        Assert.True(ctx.IsOverdue);
        Assert.True(ctx.TimeUntilDeadline < TimeSpan.Zero);
    }

    [Fact]
    public void Future_deadline_is_not_overdue()
    {
        var ctx = new FakeCtx(DateTime.UtcNow.AddMinutes(5));
        Assert.False(ctx.IsOverdue);
        Assert.True(ctx.TimeUntilDeadline > TimeSpan.Zero);
    }
}
