using System.Text.Json;
using Acta;
using Acta.Concepts.ContinuousSynchronization;
using Acta.Sqlite;
using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The lab's claims, each driven through the real runtime on a throwaway SQLite file: the schedule is forced due, one
/// admission pass admits the firing, one run executes it. No loop runs and nothing sleeps, so a firing is a line of
/// the test rather than five seconds of wall clock.
/// </summary>
public sealed class ContinuousSynchronizationTests : IAsyncLifetime
{
    private const string Namespace = "continuous-sync";
    private static readonly ScheduleLookup Schedule = new(Namespace, "sync-orders", "every-5s");

    private readonly string _schema = "acta_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly TestCrashSignal _crash = new();
    private readonly ShopOutage _outage = new();
    private IActaTestHost _host = null!;
    private ShopFeed _shop = null!;
    private OrderStore _store = null!;
    private string _connectionString = null!;
    private long _slotId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The feed and the orders live in the same file as Acta's tables, as they do in the lab.
        _connectionString = $"Data Source={_schema}.db";
        _shop = new ShopFeed(_connectionString, _outage);
        _store = new OrderStore(_connectionString);
        _host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseSqlite(sqlite =>
                {
                    sqlite.ConnectionString = _connectionString;
                    sqlite.ApplyMigrationsOnStartup = true;
                });
                j.Run<ContinuousSynchronizationJobs>(Namespace);
            },
            new ActaTestHostOptions
            {
                Schema = _schema,
                ConfigureServices = services =>
                {
                    services.AddSingleton(_shop);
                    services.AddSingleton(_store);
                    services.AddSingleton<CrashSignal>(_crash);
                    // Only the lab's schedule is in the namespace, so an admission pass admits that one or nothing.
                    services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false);
                },
            },
            Ct
        );
        await _shop.EnsureCreatedAsync(Ct);
        await _store.EnsureCreatedAsync(Ct);
        var listed = await _host.Operations.Schedules.ListAsync(
            new ListSchedulesQuery(JobNamespace: Namespace, JobName: "sync-orders"),
            Ct
        );
        _slotId = (await _host.Jobs.GetJobIdAsync(JobLookup.ByRef(Assert.Single(listed.Items).JobRef!.Value), Ct))!.JobId;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete($"{_schema}.db");
    }

    [Fact]
    public async Task A_firing_syncs_one_bounded_batch_and_moves_the_watermark_to_its_last_change()
    {
        await _shop.AppendAsync(50, Ct);

        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(40, await WatermarkAsync()); // one batch of forty, then the firing returns
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(50, await WatermarkAsync());
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(50, await WatermarkAsync()); // up to date: nothing to apply, nothing moved
        Assert.Equal((40, 50), await _store.SummaryAsync(Ct));
    }

    [Fact]
    public async Task The_watermark_is_a_variable_on_the_job_and_outlives_the_firing_that_wrote_it()
    {
        await _shop.AppendAsync(20, Ct);
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());

        // Idle, no attempt running, and the watermark is still there: a row of the job's, not of the attempt's.
        Assert.Equal(JobStatusCode.Idle, (await JobAsync()).Status);
        var checkpoints = (await _host.Jobs.GetCheckpointsAsync(JobLookup.ById(_slotId), Ct))!.Items;
        var watermark = Assert.Single(checkpoints, c => c.Kind == CheckpointKindCode.Variable);
        Assert.Equal("watermark", watermark.Name);
        Assert.Equal(20, JsonSerializer.Deserialize<long>(watermark.Value!.Value.Data.Span));
    }

    [Fact]
    public async Task Repeated_firings_use_one_permanent_job_identity()
    {
        await _shop.AppendAsync(100, Ct);
        var before = (await JobAsync()).JobRef;
        var firings = new List<Guid>();

        for (var firing = 1; firing <= 3; firing++)
        {
            Assert.Equal(JobStatusCode.Idle, (await JobAsync()).Status);
            Assert.Equal(ActaRunStatus.Completed, await FireAsync());
            var job = await JobAsync();
            Assert.Equal((before, JobStatusCode.Idle, firing), (job.JobRef, job.Status, job.ExecutionNumber));
            firings.Add(await FiringIdAsync());
        }

        // Every firing was admitted by the schedule and left an envelope with a firing id of its own.
        Assert.Equal(3, (await EventsAsync(EventCode.ScheduleAdmitted)).Count);
        Assert.Equal(3, firings.Distinct().Count());
    }

    [Fact]
    public async Task The_schedule_cursor_and_the_watermark_move_apart()
    {
        // A firing with nothing to sync still moves Acta's cursor; the job's watermark stays.
        var forcedAt = DateTime.UtcNow;
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        var afterFiring = await NextOccurrenceAsync();
        Assert.True(afterFiring > forcedAt, "the cursor moved past the forced occurrence");
        Assert.Equal(0, await WatermarkAsync());

        // A manual trigger syncs the backlog and moves the watermark; the cursor stays where the firing left it.
        await _shop.AppendAsync(10, Ct);
        Assert.Equal(ControlAction.Applied, (await _host.Operations.Schedules.TriggerNowAsync(Schedule, ct: Ct)).Action);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(_slotId, Ct));
        Assert.Equal(10, await WatermarkAsync());
        Assert.Equal(afterFiring, await NextOccurrenceAsync());
    }

    [Fact]
    public async Task A_lost_attempt_is_reclaimed_and_the_next_execution_continues_at_the_watermark()
    {
        await _shop.AppendAsync(60, Ct);
        _crash.CrashAfter = 40;

        // The attempt applies its batch, writes the watermark, and then stalls at the crash point, holding its lease.
        await AdmitAsync();
        var lost = _host.RunOnceAsync(_slotId, Ct);
        await _crash.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(40, await WatermarkAsync());
        var firing = await FiringIdAsync();

        // The lease lapses and recovery reclaims the firing: the slot is Ready again, the attempt charged.
        await _host.ExpireExecutionLeaseAsync(_slotId, Ct);
        Assert.Equal(1, (await _host.RunRecoveryOnceAsync(Namespace, Ct)).ReclaimedJobs);
        Assert.Equal(JobStatusCode.Ready, (await JobAsync()).Status);

        // The dead attempt ends and its late outcome is fenced out. The next execution re-enters from the top, reads
        // the watermark the lost one wrote, and applies the batch it owed: in the same drive, which re-claims the
        // reclaimed row, or in a fresh one when that drive claimed nothing more.
        _crash.Release.TrySetResult();
        var outcome = await lost;
        if (outcome != ActaRunStatus.Completed)
        {
            Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(_slotId, Ct));
        }
        var job = await JobAsync();
        Assert.Equal((JobStatusCode.Idle, 2), (job.Status, job.ExecutionNumber));
        Assert.Equal(60, await WatermarkAsync());
        Assert.Equal((40, 60), await _store.SummaryAsync(Ct));

        var orphaned = await EventsAsync(EventCode.JobExecutionOrphaned);
        Assert.Equal(1, Assert.Single(orphaned).ExecutionNumber);
        Assert.Single(await EventsAsync(EventCode.ScheduleAdmitted)); // one firing, two executions
        Assert.Equal(firing, await FiringIdAsync()); // the same firing id in both executions
    }

    [Fact]
    public async Task A_failed_attempt_keeps_the_watermark_it_wrote_for_the_retry()
    {
        await _shop.AppendAsync(60, Ct);
        _crash.CrashAfter = 40;
        _crash.Release.TrySetResult(); // the crash point throws at once: the attempt fails with budget left

        Assert.Equal(ActaRunStatus.Rearmed, await FireAsync());
        Assert.Equal(40, await WatermarkAsync());

        // The retry is the same firing, one execution later; it reads 40 and applies the twenty changes after it.
        await _host.ForceJobDueAsync(_slotId, Ct);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(_slotId, Ct));
        var job = await JobAsync();
        Assert.Equal((JobStatusCode.Idle, 2), (job.Status, job.ExecutionNumber));
        Assert.Equal(60, await WatermarkAsync());
        Assert.Equal((40, 60), await _store.SummaryAsync(Ct));
        Assert.Single(await EventsAsync(EventCode.ScheduleAdmitted));
    }

    [Fact]
    public async Task A_replayed_batch_changes_nothing()
    {
        await _shop.AppendAsync(20, Ct);
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        var synced = await _store.SummaryAsync(Ct);

        await _store.ApplyAsync(await _shop.ChangesAfterAsync(0, 20, Ct), Ct);

        Assert.Equal(synced, await _store.SummaryAsync(Ct));
    }

    [Fact]
    public async Task A_batch_that_fails_mid_apply_advances_nothing()
    {
        await _shop.AppendAsync(5, Ct);
        await _shop.AppendAsync([new OrderChange(0, "ORD-1005", "paid", -1)], Ct);

        Assert.Equal(ActaRunStatus.Rearmed, await FireAsync()); // the attempt failed with budget left
        Assert.Equal(0, await WatermarkAsync());
        Assert.Equal((0, 0), await _store.SummaryAsync(Ct));
    }

    [Fact]
    public async Task Changes_that_accumulated_while_nothing_fired_are_drained_by_the_following_firings()
    {
        await _shop.AppendAsync(100, Ct);

        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(40, await WatermarkAsync());
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(80, await WatermarkAsync());
        Assert.Equal(ActaRunStatus.Completed, await FireAsync());
        Assert.Equal(100, await WatermarkAsync());
    }

    private async Task AdmitAsync()
    {
        await _host.ForceScheduleDueAsync(Schedule, Ct);
        Assert.Equal(1, (await _host.AdmitOnceAsync(Namespace, Ct)).Admitted);
    }

    private async Task<ActaRunStatus> FireAsync()
    {
        await AdmitAsync();
        return await _host.RunOnceAsync(_slotId, Ct);
    }

    private async Task<GetJobResponse> JobAsync() => (await _host.Jobs.GetAsync(JobLookup.ById(_slotId), Ct))!;

    /// <summary>The watermark as the operator sees it, 0 before the first batch: the slot's one variable.</summary>
    private async Task<long> WatermarkAsync()
    {
        var checkpoints = (await _host.Jobs.GetCheckpointsAsync(JobLookup.ById(_slotId), Ct))!.Items;
        var watermark = checkpoints.SingleOrDefault(c => c.Kind == CheckpointKindCode.Variable && c.Name == "watermark");
        return watermark is null ? 0 : JsonSerializer.Deserialize<long>(watermark.Value!.Value.Data.Span);
    }

    /// <summary>
    /// The firing id from the envelope the admitter wrote, read from <c>checkpoints_view</c> as the lab prints it: the
    /// envelope is the scheduler's, so the handler-state read leaves it out.
    /// </summary>
    private async Task<Guid> FiringIdAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_text FROM checkpoints_view WHERE job_id = @job AND kind = 'firing'";
        command.Parameters.AddWithValue("@job", _slotId);
        var envelope = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(Ct))!);
        return envelope.RootElement.GetProperty("firingId").GetGuid();
    }

    private async Task<DateTime?> NextOccurrenceAsync() =>
        Assert
            .Single(
                (
                    await _host.Operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: Namespace, JobName: "sync-orders"), Ct)
                ).Items
            )
            .NextOccurrenceAtUtc;

    private async Task<IReadOnlyList<ListEventsItem>> EventsAsync(EventCode code) =>
        (await _host.Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: _slotId, EventCode: code, PageSize: 100), Ct)).Items;

    /// <summary>
    /// The lab's crash point, as a test can hold it: the attempt that wrote the watermark at CrashAfter stalls there
    /// until the test releases it, then dies like a lost process, with no completion of its own. Released in advance,
    /// it dies at once, which is an ordinary failed attempt.
    /// </summary>
    private sealed class TestCrashSignal : CrashSignal
    {
        public long? CrashAfter { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task AfterBatchAsync(long committedChangeId, CancellationToken ct)
        {
            if (committedChangeId != CrashAfter)
            {
                return;
            }

            CrashAfter = null;
            Reached.TrySetResult();
            await Release.Task;
            throw new InvalidOperationException("The worker is gone.");
        }
    }
}
