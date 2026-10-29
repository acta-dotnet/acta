using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// The envelope read an attempt makes before it runs a permanent slot: under its execution fence it sees the firing it
/// holds, and nothing once another attempt holds the job, so a stale attempt never runs with the envelope of the
/// firing that replaced its own. An ordinary job has no envelope.
/// </summary>
[ConformanceSpec(
    "execution.get-firing",
    "An attempt reads its firing's envelope under its fence",
    Area = "Execution",
    Contract = "The envelope read answers the firing the fenced attempt holds, and nothing for a lost fence or an ordinary job."
)]
public sealed class GetFiringSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private IJobs Jobs => Services.GetRequiredService<IJobs>();

    [Fact(DisplayName = "The fenced attempt reads the manual firing it holds, and a lost fence reads nothing")]
    public async Task The_fenced_attempt_reads_its_envelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("firing");
        var slotId = await RegisterAsync(jobName, DateTime.UtcNow.AddHours(6), ct);
        var lookup = new ScheduleLookup(TestNamespace, jobName, "only");
        Assert.Equal(
            ControlAction.Applied,
            (await Services.GetRequiredService<IActaOperations>().Schedules.TriggerNowAsync(lookup, ct: ct)).Action
        );
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, slotId, ct);

        var read = await ReadAsync(fence, ct);

        Assert.NotNull(read);
        Assert.Equal(FiringEnvelope.ManualKind, read.Firing.Kind);
        Assert.Empty(read.Firing.Contributors);
        Assert.Empty(read.Firing.TriggeringSchedules);

        // Another attempt's number, or another worker's lease, is no longer this attempt's job.
        Assert.Null(await ReadAsync(fence with { ExecutionNumber = fence.ExecutionNumber + 1 }, ct));
        Assert.Null(await ReadAsync(fence with { WorkerId = fence.WorkerId + 1 }, ct));
    }

    [Fact(DisplayName = "An ordinary job's attempt reads no envelope")]
    public async Task An_ordinary_job_has_no_envelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("plain");
        await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.Json(1)), ct);
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);

        Assert.Null(await ReadAsync(fence, ct));
    }

    private Task<GetFiringResponse?> ReadAsync(ExecutionFence fence, CancellationToken ct) =>
        Services.GetRequiredService<GetFiringHandler>().HandleAsync(new GetFiringQuery(fence), ct);

    private async Task<long> RegisterAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules: [new SlotSchedule("only", Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor)]
        );
        return (await ScheduleTestOps.RegisterAsync(Services, [definition], ct))[defId];
    }

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
