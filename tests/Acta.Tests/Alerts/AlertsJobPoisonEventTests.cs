using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Services.Time;
using Acta.Tests.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertsJobPoisonEventTests
{
    [Fact]
    public async Task Deterministic_bad_event_is_durably_skipped_and_later_events_project()
    {
        var store = new PoisonAlertStore(deterministicFailure: true);
        var logger = new RecordingLogger();
        var job = CreateJob(store, logger);
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        await job.Handle(ctx, ct);

        Assert.Equal([11L, 12L], store.RaiseAttempts);
        Assert.Equal(12L, await ctx.GetRequiredVariableAsync<long>("alerts-cursor", ct));
        var skip = await ctx.GetRequiredVariableAsync<string>("alerts-skip-11", ct);
        Assert.Contains("namespace=test-ns", skip);
        Assert.Contains("eventId=11", skip);
        Assert.Contains("reason=unknown-job", skip);
        Assert.Contains(LogLevel.Warning, logger.Levels);

        await job.Handle(ctx, ct);
        Assert.Equal([11L, 12L], store.RaiseAttempts);
    }

    [Fact]
    public async Task Transient_projection_failure_does_not_advance_cursor_or_create_skip()
    {
        var store = new PoisonAlertStore(deterministicFailure: false);
        var job = CreateJob(store, new RecordingLogger());
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TimeoutException>(() => job.Handle(ctx, ct));

        Assert.Equal(0L, await ctx.GetVariableOrDefaultAsync("alerts-cursor", 0L, ct));
        Assert.False(await ctx.ExistsVariableAsync("alerts-skip-11", ct));
    }

    [Fact]
    public async Task Unrelated_ArgumentException_fails_the_pass_instead_of_being_poison_skipped()
    {
        // An ArgumentException that is NOT the provider's unknown-job shape (different ParamName)
        // is an internal bug, not malformed ledger data: the pass must fail without advancing the
        // cursor or recording a skip, so the event is retried once the bug is fixed.
        var store = new PoisonAlertStore(deterministicFailure: false, failure: new ArgumentException("internal bug", "batchSize"));
        var job = CreateJob(store, new RecordingLogger());
        var ctx = new RecordingJobContext();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => job.Handle(ctx, ct));

        Assert.Equal(0L, await ctx.GetVariableOrDefaultAsync("alerts-cursor", 0L, ct));
        Assert.False(await ctx.ExistsVariableAsync("alerts-skip-11", ct));
    }

    private static AlertsJob CreateJob(IAlertStore store, ILogger<AlertsJob> logger) =>
        new(store, new FixedClock(), channels: null!, transports: null!, Options.Create(new JobsOptions()), logger);

    private sealed class PoisonAlertStore(bool deterministicFailure, Exception? failure = null) : IAlertStore
    {
        private static readonly AlertableEvent[] Events = [Event(11, 101), Event(12, 102)];

        public List<long> RaiseAttempts { get; } = [];

        public Task<int> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct)
        {
            var eventId = command.JobId == 101 ? 11L : 12L;
            RaiseAttempts.Add(eventId);
            return command.JobId == 101
                ? Task.FromException<int>(
                    failure
                        ?? (
                            deterministicFailure
                                ? new ArgumentException("The referenced job id does not exist.", "jobId")
                                : new TimeoutException("provider timeout")
                        )
                )
                : Task.FromResult(1);
        }

        public Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
            short namespaceId,
            long cursorEventId,
            int batchSize,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<AlertableEvent>>(Events.Where(e => e.EventId > cursorEventId).Take(batchSize).ToArray());

        public Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(short namespaceId, int batchSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliverableAlert>>([]);

        public Task UpdateAlertDeliveryAsync(
            long alertId,
            AlertDeliveryStatusCode status,
            byte retryCount,
            DateTime? retryAfterUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> ResolveJobAlertsAsync(short namespaceId, long jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<AlertControlOutcome> AcknowledgeJobAlertAsync(AlertControlCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AlertControlOutcome> ResolveJobAlertManualAsync(AlertControlCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AlertPage> ListJobAlertsAsync(AlertPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        private static AlertableEvent Event(long eventId, long jobId) =>
            new(
                eventId,
                jobId,
                DefinitionId: 7,
                JobName: "probe",
                AlertProfile: JobAlertProfileCode.OnTerminal,
                AlertChannelName: null,
                ExecutionStatus: ExecutionStatusCode.Failed,
                ToStatus: JobStatusCode.Failed,
                ReasonCode: JobEventReasonCode.JobUnhandledException,
                ReasonMessage: "boom"
            );
    }

    private sealed class FixedClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UnixEpoch);
    }

    private sealed class RecordingLogger : ILogger<AlertsJob>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Levels.Add(logLevel);
    }
}
