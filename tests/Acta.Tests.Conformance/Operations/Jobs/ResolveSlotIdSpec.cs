using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// A scheduled definition's permanent slot is found by the definition's path and the row's kind, never by a key: the
/// slot has none, and an ordinary job keyed by the job name is unrelated to it.
/// </summary>
[ConformanceSpec(
    "job.resolve-slot",
    "A permanent slot resolves by its definition's path, not by any key",
    Area = "Jobs",
    Contract = "A scheduled definition's slot resolves by namespace and job name through the definition and the row's kind, and an unknown path resolves to nothing."
)]
public sealed class ResolveSlotIdSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    [Fact(DisplayName = "The slot resolves by path beside an ordinary job keyed by the job name, and an unknown path resolves to null")]
    public async Task The_slot_resolves_by_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("slot");
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];
        var ordinary = await Services
            .GetRequiredService<IJobs>()
            .EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.Json(1), DeduplicationKey: jobName), ct);
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules:
            [
                new SlotSchedule(
                    "only",
                    Cron5,
                    null,
                    MisfireStrategyCode.Skip,
                    ScheduleExpressionKindCode.Cron,
                    null,
                    DateTime.UtcNow.AddHours(6)
                ),
            ]
        );
        var slotId = (await ScheduleTestOps.RegisterAsync(Services, [definition], ct))[defId];

        Assert.Equal(slotId, await ResolveAsync(TestNamespace, jobName, ct));
        Assert.NotEqual(ordinary.JobId, slotId);
        Assert.Null(await ResolveAsync(TestNamespace, Unique("no-such-job"), ct));
        Assert.Null(await ResolveAsync(Unique("no-such-namespace"), jobName, ct));
    }

    private ValueTask<long?> ResolveAsync(string jobNamespace, string jobName, CancellationToken ct) =>
        Services.GetRequiredService<ResolveSlotIdHandler>().HandleAsync(new ResolveSlotIdQuery(jobNamespace, jobName), ct);

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
