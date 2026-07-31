using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the two delayed-enqueue channels: a relative <c>Delayed</c> delay is resolved on
/// the database clock (<c>db_now + delay</c>) so the caller's clock never reaches the wire, while an
/// absolute <c>NextExecutionAt</c> persists the caller-supplied instant verbatim. The two are mutually
/// exclusive and enqueue rejects a row that sets both.
/// </summary>
[ConformanceSpec(
    "relative-delay.db-clock",
    "Relative delay resolves on the DB clock; absolute run-at is preserved",
    Area = "Enqueue",
    Contract = "Relative Delayed enqueue sends only an integer delay the server resolves as db_now plus delay, and NextExecutionAt persists the absolute caller instant.",
    Arrange = "The add-numbers job definition is registered in the test namespace.",
    Act = "Jobs are enqueued with a relative delay, an absolute run-at, a Local-kind run-at, and with both delay channels set at once.",
    Assert = "The relative delay resolves to the database clock plus the delay, absolute instants persist verbatim, and setting both channels is rejected."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class RelativeDelayUsesDbClockSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Relative delay resolves next_run_at_utc to db_now plus the delay, not the caller clock")]
    public async Task Relative_delay_is_resolved_on_the_db_clock_not_the_caller_clock()
    {
        var ct = TestContext.Current.CancellationToken;

        // Bracket the insert with two DB-clock reads. next_run = db_now(at insert) + 60s must fall
        // between them, anchoring the schedule to the database clock regardless of the caller's clock.
        // The one-second slack absorbs the next_run_at_utc column's coarser precision (SQL Server
        // datetime2(3)) versus the datetime2(7) clock read; it still firmly distinguishes "db_now + 60"
        // from an immediate or caller-clock instant.
        var before = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(2, 2), new JobEnqueueOptions { DelaySeconds = 60 }, ct);
        var after = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.InRange(job.NextRunAtUtc!.Value, before.AddSeconds(59), after.AddSeconds(61));
    }

    [Fact(DisplayName = "Absolute NextExecutionAt persists the caller instant verbatim")]
    public async Task Absolute_run_at_is_persisted_verbatim()
    {
        var ct = TestContext.Current.CancellationToken;

        var instant = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(3, 4), new JobEnqueueOptions { NextRunAtUtc = instant }, ct);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(instant, job.NextRunAtUtc!.Value);
    }

    [Fact(DisplayName = "Local-kind run-at is converted to UTC, not relabeled")]
    public async Task Local_kind_run_at_is_converted_to_utc_not_relabeled()
    {
        var ct = TestContext.Current.CancellationToken;

        // The stored column is UTC: a caller-supplied Local-kind instant must be converted
        // (ToUniversalTime), not relabeled (SpecifyKind Utc). On a non-UTC machine relabeling stores the
        // wrong instant; the converted value is correct on every machine and time zone.
        var local = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(5, 6), new JobEnqueueOptions { NextRunAtUtc = local }, ct);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(local.ToUniversalTime(), job.NextRunAtUtc!.Value);
    }

    [Fact(DisplayName = "Setting both delay channels is rejected before any SQL")]
    public async Task Setting_both_channels_is_rejected_before_any_sql()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.None)
        {
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(5),
            DelaySeconds = 60,
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.EnqueueAsync(request, ct));
    }

    [Fact(DisplayName = "Builders map relative delay to an integer, round sub-second up, and clear the other channel last-write-wins")]
    public void Builders_map_relative_delay_to_an_integer_and_clear_the_absolute_instant()
    {
        // The relative path puts no caller-computed instant on the wire: only the integer delay.
        var options = new JobEnqueueOptionsBuilder().Delayed(TimeSpan.FromSeconds(90)).Build();
        Assert.Equal(90, options.DelaySeconds);
        Assert.Null(options.NextRunAtUtc);

        var request = JobRequestBuilder.Create(TestNamespace, "add-numbers").NoPayload().Delayed(TimeSpan.FromSeconds(90)).Build();
        Assert.Equal(90, request.DelaySeconds);
        Assert.Null(request.NextRunAtUtc);

        // Sub-second delays round up so a positive delay never collapses to immediate.
        var rounded = new JobEnqueueOptionsBuilder().Delayed(TimeSpan.FromMilliseconds(1)).Build();
        Assert.Equal(1, rounded.DelaySeconds);

        // The two channels are last-write-wins: each setter clears the other.
        var absolute = new JobEnqueueOptionsBuilder().Delayed(TimeSpan.FromSeconds(5)).NextExecutionAt(DateTimeOffset.UnixEpoch).Build();
        Assert.Null(absolute.DelaySeconds);
        Assert.NotNull(absolute.NextRunAtUtc);
    }
}
