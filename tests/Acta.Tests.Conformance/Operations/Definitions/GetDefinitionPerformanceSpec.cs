using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for <c>GetDefinitionPerformance</c>: which events the read counts, the interval and the duration group
/// it places each in, and what its limit leaves out.
/// </summary>
[ConformanceSpec(
    "get-definition-performance.counts-and-limit",
    "GetDefinitionPerformance counts executions by interval, outcome and duration",
    Area = "Reads",
    Contract = "GetDefinitionPerformance counts a range's succeeded and failed executions with a duration, by interval and two-figure duration, newest first up to its limit."
)]
public sealed class GetDefinitionPerformanceSpec : ActaStorageTestBase<ConformanceFixture>
{
    /// <summary>The instant every stamp is staged from, so a range and its intervals are exact inputs rather than clock reads.</summary>
    private static readonly DateTime T0 = new(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly GetDefinitionPerformanceExecutions None = new(0, null, null, null);

    private string JobName = null!;
    private int DefId;
    private long JobIdValue;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        JobName = Unique("timed");
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, JobName, ct: ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(
        DisplayName = "Succeeded and failed executions are counted with their percentiles: a terminal failure is a failed one, and a yielded, an orphaned and an untimed one are not counted"
    )]
    public async Task Finished_executions_are_counted_by_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherDefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, Unique("other"), ct: ct);

        await StageAsync(EventCode.JobExecutionSucceeded, 12, T0, ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 120, T0, ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 1234, T0, ct);
        await StageAsync(EventCode.JobExecutionSucceeded, int.MaxValue, T0, ct);
        await StageAsync(EventCode.JobExecutionFailed, 300, T0, ct);
        await StageAsync(EventCode.JobFailed, 7654, T0, ct);

        await StageAsync(EventCode.JobExecutionOrphaned, 50, T0, ct);
        await StageAsync(EventCode.JobExecutionYielded, 60, T0, ct);
        await StageAsync(EventCode.JobFailed, null, T0, ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 9, T0, ct, definitionId: otherDefId);

        var performance = await GetAsync(T0, T0.AddMinutes(1), 60, ct);

        // Nearest rank over 12, 120, 1200 and 2,100,000,000: the second is the median and the fourth is both tails.
        Assert.Equal(new GetDefinitionPerformanceExecutions(4, 120, 2_100_000_000, 2_100_000_000), performance.Succeeded);
        Assert.Equal(new GetDefinitionPerformanceExecutions(2, 300, 7600, 7600), performance.Failed);
        var interval = Assert.Single(performance.Intervals);
        Assert.Equal(performance.Succeeded, interval.Succeeded);
        Assert.Equal(performance.Failed, interval.Failed);
        Assert.False(performance.ExecutionsHasMore);
    }

    [Fact(DisplayName = "The range takes an execution that finished at fromUtc and leaves one that finished at toUtc")]
    public async Task Range_is_inclusive_at_its_start_and_exclusive_at_its_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var toUtc = T0.AddMinutes(1);
        await StageAsync(EventCode.JobExecutionSucceeded, 1, T0.AddMilliseconds(-1), ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 2, T0, ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 3, toUtc.AddMilliseconds(-1), ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 4, toUtc, ct);

        var performance = await GetAsync(T0, toUtc, 60, ct);

        // Below 10 ms a bin is one millisecond, so the bins name the durations that were counted.
        Assert.Equal([2L, 3L], performance.SucceededBins.Select(static b => b.FromMs));
        Assert.Equal(T0, performance.OldestAtUtc);
    }

    [Fact(
        DisplayName = "Every interval of the range is answered, an empty one and a last one the range cuts short included, from a start truncated to the millisecond"
    )]
    public async Task Executions_fall_in_the_interval_they_finished_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await StageAsync(EventCode.JobExecutionSucceeded, 10, T0, ct);
        await StageAsync(EventCode.JobExecutionFailed, 20, T0.AddSeconds(60).AddMilliseconds(-1), ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 30, T0.AddSeconds(60), ct);
        await StageAsync(EventCode.JobExecutionSucceeded, 40, T0.AddSeconds(210).AddMilliseconds(-1), ct);

        // A start 0.4 ms past T0 is T0, and 210 seconds are three whole minutes and half of a fourth.
        var performance = await GetAsync(T0.AddTicks(4000), T0.AddSeconds(210), 60, ct);

        Assert.Equal(T0, performance.FromUtc);
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(3)], performance.Intervals.Select(static i => i.StartAtUtc));
        Assert.Equal([1L, 1L, 0L, 1L], performance.Intervals.Select(static i => i.Succeeded.ExecutionCount));
        Assert.Equal([1L, 0L, 0L, 0L], performance.Intervals.Select(static i => i.Failed.ExecutionCount));
        Assert.Equal(None, performance.Intervals[2].Succeeded);
        Assert.Equal(new GetDefinitionPerformanceExecutions(1, 40, 40, 40), performance.Intervals[3].Succeeded);
    }

    [Fact(
        DisplayName = "A read that fills its limit counted the newest executions only, and a range that holds exactly the limit reads as filled too"
    )]
    public async Task Limit_keeps_the_newest_executions()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var second = 0; second < 5; second++)
        {
            await StageAsync(EventCode.JobExecutionSucceeded, 10 + second, T0.AddSeconds(second), ct);
        }

        var cut = await ReadAsync(3, ct);
        var filled = await ReadAsync(5, ct);
        var whole = await ReadAsync(6, ct);

        Assert.True(cut.ExecutionsHasMore);
        Assert.Equal(3, cut.Succeeded.ExecutionCount);
        Assert.Equal(T0.AddSeconds(2), cut.OldestAtUtc);
        Assert.True(filled.ExecutionsHasMore);
        Assert.Equal(T0, filled.OldestAtUtc);
        Assert.False(whole.ExecutionsHasMore);
        Assert.Equal(5, whole.Succeeded.ExecutionCount);
    }

    [Fact(DisplayName = "A range of 31 days in one interval answers")]
    public async Task A_month_in_one_interval_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        await StageAsync(EventCode.JobExecutionSucceeded, 10, T0.AddDays(31).AddMilliseconds(-1), ct);

        var performance = await GetAsync(T0, T0.AddDays(31), 31 * 86_400, ct);

        Assert.Equal(1, Assert.Single(performance.Intervals).Succeeded.ExecutionCount);
    }

    [Fact(
        DisplayName = "A definition with no finished execution in the range answers every interval empty, and an unknown one answers null"
    )]
    public async Task No_executions_is_an_empty_answer_and_no_definition_is_null()
    {
        var ct = TestContext.Current.CancellationToken;

        var performance = await GetAsync(T0, T0.AddMinutes(2), 60, ct);

        Assert.Equal(None, performance.Succeeded);
        Assert.Equal(None, performance.Failed);
        Assert.Empty(performance.SucceededBins);
        Assert.Empty(performance.FailedBins);
        Assert.Equal([None, None], performance.Intervals.Select(static i => i.Succeeded));
        Assert.Null(performance.OldestAtUtc);
        Assert.False(performance.ExecutionsHasMore);
        Assert.Null(await HandleAsync(Unique("unknown"), T0, T0.AddMinutes(2), 60, ct));
    }

    [Fact(
        DisplayName = "A range that ends at or before its start, an interval under a minute, and a range of more than 60 intervals are caller errors"
    )]
    public async Task A_range_the_read_does_not_answer_is_a_caller_error()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidQueryException>(() => GetAsync(T0, T0, 60, ct));
        // A range shorter than a millisecond is empty once its bounds are truncated.
        await Assert.ThrowsAsync<InvalidQueryException>(() => GetAsync(T0, T0.AddTicks(4000), 60, ct));
        await Assert.ThrowsAsync<InvalidQueryException>(() => GetAsync(T0, T0.AddMinutes(-1), 60, ct));
        await Assert.ThrowsAsync<InvalidQueryException>(() => GetAsync(T0, T0.AddMinutes(1), 59, ct));
        await Assert.ThrowsAsync<InvalidQueryException>(() => GetAsync(T0, T0.AddMinutes(60).AddMilliseconds(1), 60, ct));
        Assert.Equal(60, (await GetAsync(T0, T0.AddMinutes(60), 60, ct)).Intervals.Count);
    }

    private async Task<GetDefinitionPerformanceResponse> GetAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int intervalSeconds,
        CancellationToken ct
    ) => Assert.IsType<GetDefinitionPerformanceResponse>(await HandleAsync(JobName, fromUtc, toUtc, intervalSeconds, ct));

    private ValueTask<GetDefinitionPerformanceResponse?> HandleAsync(
        string jobName,
        DateTime fromUtc,
        DateTime toUtc,
        int intervalSeconds,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<GetDefinitionPerformanceHandler>()
            .HandleAsync(new GetDefinitionPerformanceQuery(TestNamespace, jobName, fromUtc, toUtc, intervalSeconds), ct);

    /// <summary>The five seconds from T0 in one interval, read with <paramref name="executionLimit"/> in place of the handler's.</summary>
    private async Task<GetDefinitionPerformanceResponse> ReadAsync(int executionLimit, CancellationToken ct)
    {
        var query = new GetDefinitionPerformanceQuery(TestNamespace, JobName, T0, T0.AddMinutes(1), 60);
        var rows = await Services.GetRequiredService<GetDefinitionPerformanceHandler>().ReadAsync(DefId, query, executionLimit, ct);
        return GetDefinitionPerformanceHandler.Map(query, rows, executionLimit);
    }

    /// <summary>One event of the definition's job stamped <paramref name="stampUtc"/>; a null <paramref name="durationMs"/> is an execution no worker timed.</summary>
    private Task<long> StageAsync(EventCode code, int? durationMs, DateTime stampUtc, CancellationToken ct, int? definitionId = null) =>
        StagedEvents.InsertAsync(
            Db,
            new EventEntity
            {
                EventCode = code,
                NamespaceId = TestNamespaceId,
                EntityCode = EntityCode.Job,
                EntityId = JobIdValue,
                DefinitionId = definitionId ?? DefId,
                ExecutionNumber = 1,
                DurationMs = durationMs,
                EventMessage = "staged",
            },
            null,
            ct,
            stampUtc
        );
}
