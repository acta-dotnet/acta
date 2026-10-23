using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the <c>sys.alerts</c> replay read: the skipped events it reads back, and when a newer success supersedes one.</summary>
[ConformanceSpec(
    "get-poison-alert-events.replay-read",
    "The replay read returns skipped events, marking those a newer success supersedes",
    Area = "Alerts",
    Contract = "GetPoisonAlertEvents reads the named (event, job) pairs of one namespace with their alert context, superseded when a newer success of the same job exists."
)]
public sealed class GetPoisonAlertEventsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime T0 = new(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private int DefId;
    private long JobIdValue;
    private long OtherJobId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "poison-def", AlertProfileCode.OnFailure, ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
        (OtherJobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(DisplayName = "A skipped failure reads back with its job's alert context until a newer success of that job supersedes it")]
    public async Task Skipped_failure_reads_back_until_a_newer_success_supersedes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = await StageAsync(JobIdValue, EventCode.JobExecutionFailed, T0, ct);

        // Another job's success says nothing about this job's incident.
        await StageAsync(OtherJobId, EventCode.JobExecutionSucceeded, T0.AddSeconds(1), ct);
        var row = Assert.Single(await ReadAsync(TestNamespaceId, (failure, JobIdValue), ct));
        Assert.Equal((failure, JobIdValue, DefId), (row.EventId, row.JobId, row.DefinitionId));
        Assert.Equal("poison-def", row.JobName);
        Assert.Equal(AlertProfileCode.OnFailure, row.AlertProfile);
        Assert.Equal(EventReason.UnhandledException, row.Reason());
        Assert.False(row.Superseded);

        await StageAsync(JobIdValue, EventCode.JobExecutionSucceeded, T0.AddSeconds(2), ct);
        Assert.True(Assert.Single(await ReadAsync(TestNamespaceId, (failure, JobIdValue), ct)).Superseded);
    }

    [Fact(DisplayName = "A skip whose event is gone, names another job, or belongs to another namespace reads nothing")]
    public async Task Mismatched_skips_read_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = await StageAsync(JobIdValue, EventCode.JobFailed, T0, ct);
        var otherNamespace = await Seeder.SeedJobNamespaceAsync(Unique("other-ns"), ct: ct);

        Assert.Empty(await ReadAsync(TestNamespaceId, (long.MaxValue - 1, JobIdValue), ct));
        Assert.Empty(await ReadAsync(TestNamespaceId, (failure, OtherJobId), ct));
        Assert.Empty(await ReadAsync(otherNamespace, (failure, JobIdValue), ct));
    }

    private async Task<IReadOnlyList<AlertableEvent>> ReadAsync(int namespaceId, (long EventId, long JobId) skip, CancellationToken ct)
    {
        var json = new GetAlertFloorResponse(
            true,
            new AlertsCursor(0L, 0L),
            [new PoisonAlertEvent(skip.EventId, skip.JobId, T0.Ticks)]
        ).PoisonJson()!;
        return (await Services.GetRequiredService<AlertsOperations>().GetPoisonAlertEventsAsync(namespaceId, json, ct)).Events;
    }

    /// <summary>One attempt event of <paramref name="jobId"/> at <paramref name="stampUtc"/>; a failure carries its reason.</summary>
    private Task<long> StageAsync(long jobId, EventCode code, DateTime stampUtc, CancellationToken ct) =>
        StagedEvents.InsertAsync(
            Db,
            new EventEntity
            {
                EventCode = code,
                NamespaceId = TestNamespaceId,
                EntityCode = EntityCode.Job,
                EntityId = jobId,
                DefinitionId = DefId,
                ExecutionNumber = 1,
                EventMessage = "staged",
            },
            code == EventCode.JobExecutionSucceeded ? null : EventDetails.Write(("reason", EventReason.UnhandledException)),
            ct,
            stampUtc
        );
}
