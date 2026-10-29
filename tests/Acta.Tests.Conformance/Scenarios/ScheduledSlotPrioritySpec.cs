using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>Minimal handler for the priority-slot job; the spec asserts the slot's claim-order key, not side effects.</summary>
internal static class PrioritySlotHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Hand-written manifest for a single recurring job that declares <see cref="JobPriorityCode.Critical"/>.
/// Isolated from <c>TestJobsManifest</c> so sibling specs that count slots/schedules are unaffected.
/// </summary>
public sealed class ScheduledSlotPriorityManifest : IJobManifest
{
    public const string JobName = "prio-beat";
    private const string ScheduleName = "beat";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: JobName,
                HandlerType: typeof(PrioritySlotHandler),
                MethodName: nameof(PrioritySlotHandler.Run),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Critical,
                MaxAttempts: 2,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await PrioritySlotHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            )
            {
                Schedules =
                [
                    new ScheduleDescriptor(
                        JobName: JobName,
                        ScheduleName: ScheduleName,
                        Expression: "PT30S",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Interval,
                        Description: null,
                        Environments: []
                    ),
                ],
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
            },
        ]);
}

/// <summary>
/// Conformance for recurring-slot priority: the registration upsert must stamp the slot's runtime
/// claim-order key from the owning definition's effective priority, not a hardcoded Normal, and a
/// re-registration after the declared priority changes must propagate it onto the existing slot row.
/// </summary>
[ConformanceSpec(
    "schedule.slot-priority",
    "Recurring slot claims at its definition's priority",
    Area = "Scheduling",
    Contract = "A recurring slot's runtime priority is stamped from the owning definition's effective priority, and re-registration propagates a changed priority."
)]
public sealed class ScheduledSlotPrioritySpec : ActaRuntimeTestBase<ConformanceFixture, ScheduledSlotPriorityManifest>
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = ScheduledSlotPriorityManifest.JobName;

    [Fact(DisplayName = "Registration stamps the slot runtime priority from the definition's declared Critical priority")]
    public async Task Registration_stamps_slot_priority_from_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        var runtime = await Db.From<JobStateEntity>().Where(r => r.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.Equal(JobPriorityCode.Critical, runtime!.Priority);
    }

    [Fact(DisplayName = "Re-registration after the definition priority changes updates the existing slot jobs_state row")]
    public async Task Reregistration_propagates_changed_definition_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var slotId = await SlotIdAsync(ct);

        var def = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns && d.DefinitionName == JobName).SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        Assert.Equal(JobPriorityCode.Critical, def!.PriorityEffective);

        // A worker redeploy lowers the declared [Job] priority; the effective column recomputes.
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == def.Id)
            .UpdateOnlyAsync(() => new DefinitionEntity { Priority = JobPriorityCode.High }, ct);

        await ReRegisterAsync(ns, def.Id, ct);

        var runtime = await Db.From<JobStateEntity>().Where(r => r.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.Equal(JobPriorityCode.High, runtime!.Priority);
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    // Rebuilds the whole-namespace registration command from the persisted slot and reruns it through
    // the store port, exactly as a worker redeploy does, so the priority propagation goes through the routine.
    private async Task ReRegisterAsync(int ns, int definitionId, CancellationToken ct)
    {
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, ns, JobName, ct);
        var slot = await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.NotNull(slot);
        var schedules = await ScheduleTestOps.ReadAsync(Db, slotId, ct);

        var slotSchedules = schedules
            .Select(s => new SlotSchedule(
                s.ScheduleName,
                s.Expression,
                s.TimeZoneId,
                s.MisfireStrategy,
                s.ExpressionKind,
                s.Description,
                s.NextOccurrenceAtUtc
            ))
            .ToList();

        var command = new DefinitionSchedules(
            ns,
            definitionId,
            slot!.InputFormatId,
            slot.Input is null ? ReadOnlyMemory<byte>.Empty : slot.Input,
            slot.EventRecording,
            slotSchedules
        );

        await ScheduleTestOps.RegisterAsync(Services, [command], ct);
    }
}
