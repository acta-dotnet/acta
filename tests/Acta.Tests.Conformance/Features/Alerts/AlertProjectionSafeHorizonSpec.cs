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
/// <para>The horizon is staged from the database's own clock at both ends: the event is inserted with
/// the column default, so its stamp is the database's, and aging it is the only thing these facts change
/// about it. Both halves are asserted, because "not yet" is only a guarantee if "then" follows.</para>
/// </summary>
[ConformanceSpec(
    "alerts-projection.safe-horizon",
    "The alerts projector reads behind a safe horizon rather than up to the present",
    Area = "Alerts",
    Contract = "The sys.alerts projection read offers an event only once its created_at_utc is older than the safe horizon, so the cursor never steps over an uncommitted id.",
    Arrange = "One alertable failure event is written for a seeded job with the database's own created_at_utc stamp.",
    Act = "The projector passes over it while it is still inside the horizon, and again after its stamp is aged past the horizon.",
    Assert = "The first pass projects nothing and leaves the cursor unwritten, and the second projects the event and checkpoints its id."
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
        await StageFailureEventAsync(subject, ct);

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
        var eventId = await StageFailureEventAsync(subject, ct);

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
    private Task<long> StageFailureEventAsync(AlertingSubject subject, CancellationToken ct) =>
        Db.From<JobEvent>()
            .InsertAsync<long>(
                new JobEvent
                {
                    EventCode = EventCode.JobExecutionFinished,
                    NamespaceId = NamespaceId,
                    ActorCode = ActorCode.Worker,
                    JobId = subject.JobId,
                    DefinitionId = subject.DefinitionId,
                    ExecutionNumber = 1,
                    ToStatus = JobStatusCode.Failed,
                    ExecutionStatus = ExecutionStatusCode.Failed,
                    ReasonCode = JobEventReasonCode.JobUnhandledException,
                    ReasonMessage = "staged inside the horizon",
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
