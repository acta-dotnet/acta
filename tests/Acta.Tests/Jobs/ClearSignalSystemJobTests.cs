using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// A clear refuses a system job before it reaches the checkpoint: a system job's signals are commands it has yet to
/// apply, such as the operator requeues and discards parked on sys.outbox.
/// </summary>
public sealed class ClearSignalSystemJobTests
{
    [Fact]
    public async Task A_system_jobs_clear_is_rejected_and_deletes_nothing()
    {
        var session = new ScriptedSession(call =>
            call.Stem == "GetJob" ? Snapshot("sys.outbox") : throw new NotSupportedException(call.Stem)
        );
        var clear = session.Services().GetRequiredService<ClearJobSignalHandler>();

        var answer = await clear.HandleAsync(
            new ClearJobSignalCommand(JobLookup.ById(7), OutboxSignalNames.Requeue, null),
            TestContext.Current.CancellationToken
        );

        Assert.Equal((ControlAction.Rejected, JobStatusCode.Idle, "system-job"), (answer.Action, answer.Status, answer.Reason));
        Assert.Equal(["GetJob"], session.Calls.Select(call => call.Stem));
    }

    [Fact]
    public async Task A_user_jobs_clear_reaches_the_store()
    {
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "GetJob" => Snapshot("send-invoice"),
                "ClearJobSignal" => new ClearJobSignalRow(ControlAction.Applied, JobStatusCode.Idle),
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var clear = session.Services().GetRequiredService<ClearJobSignalHandler>();

        var answer = await clear.HandleAsync(
            new ClearJobSignalCommand(JobLookup.ById(7), "approval", null),
            TestContext.Current.CancellationToken
        );

        Assert.Equal((ControlAction.Applied, (string?)null), (answer.Action, answer.Reason));
        Assert.Equal(["GetJob", "ClearJobSignal"], session.Calls.Select(call => call.Stem));
    }

    /// <summary>Job 7, a permanent job between firings under <paramref name="jobName"/>.</summary>
    private static GetJobResponse Snapshot(string jobName) =>
        new(
            JobId: 7,
            JobRef: JobRef.New(),
            Kind: JobKindCode.Permanent,
            LineageRootJobId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: null,
            CorrelationKey: null,
            JobNamespace: "billing",
            JobName: jobName,
            DefinitionId: 11,
            TenantId: null,
            TenantName: null,
            Status: JobStatusCode.Idle,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            LeasedByWorkerRef: null,
            Version: 3
        );
}
