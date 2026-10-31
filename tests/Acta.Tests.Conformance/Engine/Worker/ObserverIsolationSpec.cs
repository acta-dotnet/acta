using System.Diagnostics;
using System.Diagnostics.Metrics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Drives the real loops through <see cref="WorkerRuntime.RunAsync"/> with an application MeterListener that throws on
/// the measurements an idle worker records most: claim attempts, wake waits, and a due schedule's age. The listener
/// costs its measurements; the worker still claims an enqueued job and admits a due schedule.
/// </summary>
[ConformanceSpec(
    "worker.observer-isolation",
    "A metrics listener that throws never stops a worker's intake",
    Area = "Recovery",
    Contract = "A listener that throws on claim, wake-wait, or due-age measurements, once or on every call, leaves a running worker claiming jobs and admitting schedules."
)]
public sealed class ObserverIsolationSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    private static readonly HashSet<string> Throwing = ["acta.claims", "acta.wakeup.waits", "acta.schedule.due.age"];

    [Theory(DisplayName = "A listener that throws on the idle worker's measurements leaves it claiming jobs and admitting schedules")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_throwing_listener_leaves_intake_running(bool everyCall)
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var thrown = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        using var listener = new MeterListener();
        var meter = Services.GetRequiredService<ActaMetrics>().Meter;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && Throwing.Contains(instrument.Name))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Throw(Instrument instrument)
        {
            if (thrown.AddOrUpdate(instrument.Name, 1, (_, n) => n + 1) == 1 || everyCall)
            {
                throw new InvalidOperationException($"listener on {instrument.Name}");
            }
        }
        listener.SetMeasurementEventCallback<long>((i, _, _, _) => Throw(i));
        listener.SetMeasurementEventCallback<double>((i, _, _, _) => Throw(i));
        listener.Start();

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            // The idle loops record, and the listener throws, before any work arrives.
            await UntilAsync(() => thrown.ContainsKey("acta.claims") && thrown.ContainsKey("acta.wakeup.waits"), ct);

            var job = await Jobs.EnqueueAsync(
                new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
                ct
            );
            await UntilAsync(async () => (await ReadJobAsync(job.JobId, ct)).Status == JobStatusCode.Succeeded, ct);

            var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
            var due = DateTime.UtcNow.AddSeconds(-1);
            foreach (var s in await ScheduleTestOps.ReadAsync(Db, slotId, ct))
            {
                await Db.From<ScheduleStateEntity>()
                    .Where(st => st.Id == s.Config.Id)
                    .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = due, NextEvaluationAtUtc = due }, ct);
            }
            await UntilAsync(() => RecurringPingHandler.TriggersFor(TestNamespace).Count > 0, ct);

            Assert.True(thrown.ContainsKey("acta.schedule.due.age"));
            Assert.False(run.IsCompleted, "the runtime stopped");
        }
        finally
        {
            await hostCts.CancelAsync();
            try
            {
                await run.WaitAsync(Timeout, ct);
            }
            catch (OperationCanceledException) { }
        }
    }

    private static Task UntilAsync(Func<bool> condition, CancellationToken ct) => UntilAsync(() => Task.FromResult(condition()), ct);

    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(waited.Elapsed < Timeout, "the worker made no progress in time");
            await Task.Delay(20, ct);
        }
    }
}
