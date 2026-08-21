using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the safe horizon the <c>sys.alerts</c> projection read stops at. <c>events.id</c> is
/// allocated when the row is inserted, not when its transaction commits, and the alertable insert sits
/// mid-routine with blocking parent-row locks after it - so without a horizon a lower id could commit
/// after a higher one was already read and checkpointed, and nothing would ever read it again. The read
/// therefore offers an event only once its <c>created_at_utc</c>, stamped inside the writing
/// transaction, is older than the horizon.
///
/// <para>The horizon is staged from the database's own clock at both ends: an event is inserted with the
/// column default, so its stamp is the database's, and aging it is the only thing these facts change
/// about it. Withholding is asserted alone and layered under a projected event, because a cursor that
/// stops short only matters when the pass had somewhere to move it to - and "not yet" is only a
/// guarantee if "then" follows.</para>
/// </summary>
[ConformanceSpec(
    "alerts-projection.safe-horizon",
    "The alerts projector reads behind a safe horizon rather than up to the present",
    Area = "Alerts",
    Contract = "The sys.alerts projection read offers an event only once its created_at_utc is older than the safe horizon, so the cursor never steps over an uncommitted id.",
    Arrange = "Alertable failure events are written for a seeded job with the database's own created_at_utc stamps, aged past the horizon or left inside it.",
    Act = "The projector passes over them while a stamp is still inside the horizon, and again once that stamp has been aged past it.",
    Assert = "A pass projects only what is behind the horizon and stops its cursor below the withheld event, which the next pass takes once it ages out."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
public abstract class AlertProjectionSafeHorizonSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "An event still inside the horizon is not projected and the cursor does not advance")]
    public async Task Event_inside_the_horizon_is_left_for_a_later_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        await StageFailureEventAsync(subject, executionNumber: 1, ct);

        await RunAlertsWithoutAgingAsync(subject.JobId, ct);

        // Nothing projected, and - the load-bearing half - no cursor either. A pass that checkpointed
        // the horizon away would leave this event permanently behind a cursor that had passed it.
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(0, await CountVariableAsync(subject.JobId, AlertsJob.CursorVariableName, ct));
    }

    [Fact(DisplayName = "An event aged past the horizon projects on the next pass")]
    public async Task Event_aged_past_the_horizon_is_projected()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        var eventId = await StageFailureEventAsync(subject, executionNumber: 1, ct);

        await RunAlertsWithoutAgingAsync(subject.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));

        // Only the stamp moves: the same row, the same id, now old enough that every transaction which
        // could have held a lower id is provably finished.
        await AlertTestOps.AgeEventsPastHorizonAsync(Services, NamespaceId, ct);
        await RunAlertsWithoutAgingAsync(subject.JobId, ct);

        var incident = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FinalFailure, incident.Kind);
        Assert.Equal(1, incident.OccurrenceCount);
        Assert.Equal(eventId, await ReadCursorAsync(subject.JobId, ct));
    }

    [Fact(DisplayName = "A pass projects the aged event and stops its cursor below the one still inside the horizon")]
    public async Task Cursor_stops_below_the_withheld_event_rather_than_at_the_highest_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await SeedAlertingJobAsync(ct);
        var aged = await StageFailureEventAsync(subject, executionNumber: 1, ct);
        await AlertTestOps.AgeEventsPastHorizonAsync(Services, NamespaceId, ct);
        var withheld = await StageFailureEventAsync(subject, executionNumber: 2, ct);
        Assert.True(withheld > aged, "the staged events must be layered: the withheld one takes the higher id.");

        await RunAlertsWithoutAgingAsync(subject.JobId, ct);

        // The production shape, where the pass has both something to project and something to withhold,
        // so the checkpoint is a decision rather than the absence of one. Stopping at the aged id is
        // exactly what keeps the newer event readable: a cursor at the highest id that exists would have
        // stepped over it for good.
        Assert.Equal(1, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(aged, await ReadCursorAsync(subject.JobId, ct));

        // And the withheld event is deferred, not dropped: it projects on the pass after it ages out.
        await AlertTestOps.AgeEventsPastHorizonAsync(Services, NamespaceId, ct);
        await RunAlertsWithoutAgingAsync(subject.JobId, ct);

        Assert.Equal(2, Assert.Single(await ReadAlertsAsync(NamespaceId, ct)).OccurrenceCount);
        Assert.Equal(withheld, await ReadCursorAsync(subject.JobId, ct));
    }

    /// <summary>
    /// One definition carrying the <c>OnTerminal</c> profile and one job under it - the quietest
    /// alerting shape there is, so a single terminal-failure event means exactly one raise. The job
    /// doubles as the cursor's owner, standing in for the namespace's sys.alerts slot.
    /// </summary>
    private async Task<AlertingSubject> SeedAlertingJobAsync(CancellationToken ct)
    {
        var seeder = new ActaTestSeeder(Db);
        var definitionId = await seeder.SeedJobDefinitionAsync(NamespaceId, TestKey("horizon-probe"), AlertProfileCode.OnTerminal, ct);
        var (jobId, _) = await seeder.SeedJobAsync(NamespaceId, definitionId, ct: ct);
        return new AlertingSubject(definitionId, jobId);
    }

    /// <summary>
    /// One terminal-failure <c>job.execution-finished</c> row, with <c>created_at_utc</c> left to the
    /// column default so the stamp is the database's own clock rather than this process's.
    /// </summary>
    private Task<long> StageFailureEventAsync(AlertingSubject subject, int executionNumber, CancellationToken ct) =>
        Db.From<JobEvent>()
            .InsertAsync<long>(
                new JobEvent
                {
                    EventCode = EventCode.JobExecutionFinished,
                    NamespaceId = NamespaceId,
                    ActorCode = ActorCode.Worker,
                    JobId = subject.JobId,
                    DefinitionId = subject.DefinitionId,
                    ExecutionNumber = executionNumber,
                    ToStatus = JobStatusCode.Failed,
                    ExecutionStatus = ExecutionStatusCode.Failed,
                    ReasonCode = JobEventReasonCode.JobUnhandledException,
                    ReasonMessage = "staged for the horizon",
                },
                ct
            );

    // The one driver in the suite that does NOT age events first: this spec is the horizon's own, so
    // the shared helper's convenience would erase exactly what is under test.
    private Task RunAlertsWithoutAgingAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(
            Services,
            TestNamespace,
            NamespaceId,
            cursorOwnerJobId,
            options: null,
            drain: null,
            ct,
            ageEventsPastHorizon: false
        );

    private Task<long> ReadCursorAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.ReadAlertsCursorAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, ct);

    private sealed record AlertingSubject(int DefinitionId, long JobId);
}
