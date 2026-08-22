using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the <c>sys.alerts</c> generate phase's bounded drain: one invocation keeps reading
/// batches until the backlog runs out, the batch cap is reached, or the elapsed budget is spent, and
/// checkpoints its cursor after every completed batch. The backlog is staged as <c>events</c> rows
/// against a seeded definition and job rather than by running thousands of real attempts: the
/// projector reads nothing but those rows and the definition's alert policy, so staging them is the
/// same input at a fraction of the cost - and it is the only way to put more than one production batch
/// above the cursor in a spec. Every failure lands on one incident, so the row's occurrence count is a
/// running total of the events this drain actually projected.
/// </summary>
[ConformanceSpec(
    "alerts-projection.drain",
    "The alerts projector drains a backlog in bounded batches within one invocation",
    Area = "Alerts",
    Contract = "One sys.alerts generate pass drains repeated batches until the backlog empties or a bound is reached, checkpointing its cursor after every batch.",
    Arrange = "A backlog of alertable failure events is staged above the projector's cursor on one seeded job.",
    Act = "The projector runs one or more passes, under the shipped bounds and under reduced ones that make each bound reachable.",
    Assert = "A multi-batch backlog clears in one pass, each bound ends the pass at the last completed batch, and a lost checkpoint replays only the in-flight batch."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
public abstract class AlertProjectionDrainSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Small enough that a fact reaches the cap in twenty staged events, and shaped so the cap
    // (4 x 3 = 12) lands strictly inside a 20-event backlog: the pass must stop with events left, or
    // "the cap ended the pass" and "the backlog ended the pass" would be the same observation.
    private static readonly AlertDrainBudget CappedDrain = new(BatchSize: 4, MaxBatches: 3, TimeBudget: TimeSpan.FromSeconds(30));

    // The same batch size with the batch cap out of the way and the elapsed budget already spent. Zero
    // is not a stand-in for a wall clock here - it is the budget genuinely being gone by the time the
    // first check happens, which is the one decision the soft bound makes. What it proves is the
    // cooperative half: the batch in flight still completed and still checkpointed.
    private static readonly AlertDrainBudget SpentBudgetDrain = new(BatchSize: 4, MaxBatches: 40, TimeBudget: TimeSpan.Zero);

    private const int StagedBacklog = 20;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A backlog several batches deep clears in one invocation")]
    public async Task Backlog_larger_than_one_batch_drains_within_a_single_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);

        // Two full production batches and a short third, sized off the shipped constant so raising it
        // keeps this fact spanning batches instead of quietly becoming a single-batch pass.
        var backlog = (AlertsJob.DefaultDrain.BatchSize * 2) + 88;
        var staged = await StageFailureEventsAsync(subject, backlog, ct);

        await RunAlertsAsync(subject.JobId, drain: null, ct);

        // The count is the load-bearing number: one row proves the incident collapsed, and its count
        // proves every staged event was projected by this one invocation, not just the first batch.
        var incident = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FinalFailure, incident.Kind);
        Assert.Equal(backlog, incident.OccurrenceCount);
        Assert.Equal(staged[^1], await ReadCursorAsync(subject.JobId, ct));
    }

    [Fact(DisplayName = "The batch cap ends the pass at the last completed batch, and the next pass continues")]
    public async Task Batch_cap_ends_the_pass_and_forward_progress_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        var staged = await StageFailureEventsAsync(subject, StagedBacklog, ct);
        var cap = CappedDrain.BatchSize * CappedDrain.MaxBatches;

        await RunAlertsAsync(subject.JobId, CappedDrain, ct);

        // Stopped exactly at the cap - not at the backlog's end - with the cursor on the last event of
        // the last completed batch, which is the boundary the next pass has to resume from.
        Assert.Equal(cap, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(staged[cap - 1], await ReadCursorAsync(subject.JobId, ct));

        // Forward progress: the next invocation picks up behind that cursor and finishes the backlog.
        await RunAlertsAsync(subject.JobId, CappedDrain, ct);

        Assert.Equal(StagedBacklog, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(staged[^1], await ReadCursorAsync(subject.JobId, ct));
    }

    [Fact(DisplayName = "A crash between batches replays only the batch that was in flight")]
    public async Task Cursor_checkpoints_per_batch_so_a_crash_replays_one_batch_without_inflating_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        var staged = await StageFailureEventsAsync(subject, StagedBacklog, ct);
        var cap = CappedDrain.BatchSize * CappedDrain.MaxBatches;

        await RunAlertsAsync(subject.JobId, CappedDrain, ct);
        Assert.Equal(cap, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);

        // The staged crash: the last batch's alert writes all committed, then the pass died before the
        // checkpoint that would have covered them. Rewinding the cursor one batch - rather than
        // deleting it, the whole-pass crash AlertProjectionReplaySpec stages - restores exactly that
        // state, and it is the state only a per-batch checkpoint can produce.
        var lastBoundary = staged[cap - CappedDrain.BatchSize - 1];
        await AlertTestOps.RewindAlertsCursorAsync(Services, TestNamespace, NamespaceId, subject.JobId, lastBoundary, ct);

        await RunAlertsAsync(subject.JobId, CappedDrain, ct);

        // The in-flight batch is re-offered and changes nothing: the incident's mark is already at or
        // past every event in it, so those four raises are held. The count lands on the backlog's own
        // size - a replay that re-incremented would read 24 here, four above it.
        Assert.Equal(StagedBacklog, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(staged[^1], await ReadCursorAsync(subject.JobId, ct));
    }

    [Fact(DisplayName = "A spent time budget ends the pass after the batch in flight, not inside it")]
    public async Task Spent_time_budget_ends_the_pass_after_the_current_batch_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        var staged = await StageFailureEventsAsync(subject, StagedBacklog, ct);

        await RunAlertsAsync(subject.JobId, SpentBudgetDrain, ct);

        // One whole batch, then out: the budget was gone before the pass started, and the check still
        // let the batch it was in finish and checkpoint. A budget read mid-batch would have left a
        // partial count, and one read before the first batch would have left nothing at all.
        Assert.Equal(SpentBudgetDrain.BatchSize, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(staged[SpentBudgetDrain.BatchSize - 1], await ReadCursorAsync(subject.JobId, ct));

        await RunAlertsAsync(subject.JobId, SpentBudgetDrain, ct);

        Assert.Equal(SpentBudgetDrain.BatchSize * 2, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(staged[(SpentBudgetDrain.BatchSize * 2) - 1], await ReadCursorAsync(subject.JobId, ct));
    }

    [Fact(DisplayName = "An idle pass leaves no cursor and no rows behind")]
    public async Task Idle_invocation_writes_no_cursor_and_no_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);

        await RunAlertsAsync(subject.JobId, drain: null, ct);

        // The empty first read ends the pass before anything is written. The absent checkpoint row is
        // the observable half of "no loop artifacts": a drain that checkpointed an unchanged cursor,
        // or looped once more to confirm the emptiness, would have left one here.
        Assert.Equal(0, await CountVariableAsync(subject.JobId, AlertsJob.CursorVariableName, ct));
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    // ---------- staging ----------

    /// <summary>
    /// One definition carrying the <c>OnTerminal</c> profile and one job under it. <c>OnTerminal</c>
    /// with terminal-failure events is the quietest alerting shape there is: each event emits exactly
    /// one FinalFailure raise onto one incident, with no threshold escalation and no resolution to
    /// reason about, so the incident's occurrence count is a clean tally of projected events. The job
    /// doubles as the cursor's owner, standing in for the namespace's sys.alerts slot.
    /// </summary>
    private async Task<AlertingSubject> SeedAlertingJobAsync(CancellationToken ct)
    {
        var seeder = new ActaTestSeeder(Db);
        var definitionId = await seeder.SeedJobDefinitionAsync(NamespaceId, TestKey("drain-probe"), AlertProfileCode.OnTerminal, ct);
        var (jobId, _) = await seeder.SeedJobAsync(NamespaceId, definitionId, ct: ct);
        return new AlertingSubject(definitionId, jobId);
    }

    /// <summary>
    /// <paramref name="count"/> terminal-failure <c>job.execution-finished</c> rows above the
    /// projector's cursor, returned in the ascending id order the projector will read them in, so a
    /// fact can name the exact event a bound should have stopped on.
    /// </summary>
    private async Task<IReadOnlyList<long>> StageFailureEventsAsync(AlertingSubject subject, int count, CancellationToken ct)
    {
        var ids = new List<long>(count);
        for (var i = 0; i < count; i++)
        {
            var id = await Db.From<JobEvent>()
                .InsertAsync<long>(
                    new JobEvent
                    {
                        EventCode = EventCode.JobExecutionFinished,
                        NamespaceId = NamespaceId,
                        ActorCode = ActorCode.Worker,
                        JobId = subject.JobId,
                        DefinitionId = subject.DefinitionId,
                        ExecutionNumber = i + 1,
                        ToStatus = JobStatusCode.Failed,
                        ExecutionStatus = ExecutionStatusCode.Failed,
                        ReasonCode = JobEventReasonCode.JobUnhandledException,
                        ReasonMessage = "staged backlog",
                    },
                    ct
                );
            ids.Add(id);
        }

        return ids;
    }

    // ---------- driving the projector ----------

    private Task RunAlertsAsync(long cursorOwnerJobId, AlertDrainBudget? drain, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain, ct);

    private Task<long> ReadCursorAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.ReadAlertsCursorAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, ct);

    private sealed record AlertingSubject(int DefinitionId, long JobId);
}
