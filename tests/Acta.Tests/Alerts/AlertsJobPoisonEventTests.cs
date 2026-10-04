using System.Data.Common;
using Acta.Runtime;
using Acta.Tests.Context;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Equal(12L, (await ctx.GetRequiredVariableAsync<AlertsCursor>(AlertsJob.CursorVariableName, ct)).EventId);
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

        Assert.False(await ctx.ExistsVariableAsync(AlertsJob.CursorVariableName, ct));
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

        Assert.False(await ctx.ExistsVariableAsync(AlertsJob.CursorVariableName, ct));
        Assert.False(await ctx.ExistsVariableAsync("alerts-skip-11", ct));
    }

    private static AlertsJob CreateJob(PoisonAlertStore store, ILogger<AlertsJob> logger)
    {
        var alerts = store.Lifecycle();
        // The clock reads the epoch plus the real lag, which puts every staged event exactly on the horizon, so
        // this double's subject stays what it is about: which events a raise failure does and does not skip.
        var clock = new FixedClock(DateTime.UnixEpoch + alerts.SafeHorizonLag);
        return new(alerts, clock, channels: null!, transports: null!, Options.Create(new JobsOptions()), logger);
    }

    private sealed class PoisonAlertStore(bool deterministicFailure, Exception? failure = null)
    {
        private static readonly AlertableEvent[] Events = [Event(11, 101), Event(12, 102)];

        public List<long> RaiseAttempts { get; } = [];

        /// <summary>The real alert lifecycle over a session that serves the two events and fails the first raise.</summary>
        public AlertLifecycle Lifecycle() => new ScriptedSession(call =>
                call.Stem switch
                {
                    "GetAlertableEvents" => Events
                        .Where(e => e.EventId > call.Get(ActaSchema.Sql.CursorEventId))
                        .Take(call.Get(ActaSchema.Sql.AlertBatchSize))
                        .ToArray(),
                    "RaiseJobAlert" => Raise(call.Get(ActaSchema.JobAlert.JobId), call.Get(ActaSchema.Sql.SourceEventId)),
                    "GetDeliverableAlerts" => Array.Empty<DeliverableAlert>(),
                    _ => throw new NotSupportedException(call.Stem),
                }
            ).Services().GetRequiredService<AlertLifecycle>();

        // A deterministic failure is the provider's unknown-job error, which the raise handler maps to the
        // ArgumentException the projection skips.
        private AlertOccurrence Raise(long? jobId, long? sourceEventId)
        {
            RaiseAttempts.Add(jobId == 101 ? 11L : 12L);
            return jobId == 101
                ? throw failure ?? (deterministicFailure ? new UnknownJob() : new TimeoutException("provider timeout"))
                : new AlertOccurrence(1, sourceEventId);
        }

        private static AlertableEvent Event(long eventId, long jobId) =>
            new(
                eventId,
                DateTime.UnixEpoch,
                jobId,
                DefinitionId: 7,
                JobName: "probe",
                AlertProfile: AlertProfileCode.OnTerminal,
                AlertChannelName: null,
                ExecutionStatus: ExecutionStatusCode.Failed,
                ToStatus: JobStatusCode.Failed,
                ReasonCode: JobEventReasonCode.JobUnhandledException,
                ReasonMessage: "boom"
            );
    }

    private sealed class UnknownJob() : DbException("ACTA:ALERT_UNKNOWN_JOB: 101");

    private sealed class FixedClock(DateTime nowUtc) : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(nowUtc);
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
