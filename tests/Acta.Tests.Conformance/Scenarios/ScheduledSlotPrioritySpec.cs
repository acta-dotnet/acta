using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Schedules;
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
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Critical,
                MaxAttempts: 2,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await PrioritySlotHandler.Run(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
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
                        TimeZone: null,
                        Misfire: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Interval,
                        Description: null,
                        Environments: []
                    ),
                ],
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
                RecurringResultCap = 3,
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
    Contract = "A recurring slot's runtime priority is stamped from the owning definition's effective priority, and re-registration propagates a changed priority.",
    Arrange = "A recurring job declares Priority Critical and one interval schedule, registered into the worker namespace.",
    Act = "The slot is registered, then the definition priority is changed to High and the whole-namespace registration runs again.",
    Assert = "The slot runtime priority is Critical after registration and High after re-registration, tracking the definition."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
public abstract class ScheduledSlotPrioritySpec<TFixture> : ActaRuntimeTestBase<TFixture, ScheduledSlotPriorityManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = ScheduledSlotPriorityManifest.JobName;

    [Fact(DisplayName = "Registration stamps the slot runtime priority from the definition's declared Critical priority")]
    public async Task Registration_stamps_slot_priority_from_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.Equal(JobPriorityCode.Critical, runtime!.Priority);
    }

    [Fact(DisplayName = "Re-registration after the definition priority changes updates the existing slot runtime row")]
    public async Task Reregistration_propagates_changed_definition_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var slotId = await SlotIdAsync(ct);

        var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == ns && d.Name == JobName).SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        Assert.Equal(JobPriorityCode.Critical, def!.PriorityEffective);

        // A worker redeploy lowers the declared [Job] priority; the effective column recomputes.
        await Db.From<JobDefinition>()
            .Where(d => d.Id == def.Id)
            .UpdateOnlyAsync(() => new JobDefinition { Priority = JobPriorityCode.High }, ct);

        await ReRegisterAsync(ns, def.Id, ct);

        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == slotId).SingleOrDefaultAsync(ct);
        Assert.Equal(JobPriorityCode.High, runtime!.Priority);
    }

    // ---------- helpers ----------

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    // Rebuilds the whole-namespace registration command from the persisted slot and reruns it through
    // the store port, exactly as a worker redeploy does, so the priority propagation goes through the routine.
    private async Task ReRegisterAsync(short ns, int definitionId, CancellationToken ct)
    {
        var slot = await Db.From<Job>().Where(j => j.NamespaceId == ns && j.DeduplicationKey == JobName).SingleOrDefaultAsync(ct);
        Assert.NotNull(slot);
        var runtime = await Db.From<JobRuntime>().Where(r => r.Id == slot!.Id).SingleOrDefaultAsync(ct);
        var schedules = await Db.From<JobSchedule>().Where(s => s.DefinitionId == definitionId).ToListAsync(ct);

        var slotSchedules = schedules
            .Select(s => new SlotSchedule(s.Name, s.Expression, s.TimeZoneId, s.Misfire, s.ExpressionKind, s.Description, s.NextRunAtUtc))
            .ToList();

        var command = new DefinitionSchedules(
            ns,
            definitionId,
            JobName,
            slot!.InputFormatId,
            slot.Input is null ? ReadOnlyMemory<byte>.Empty : slot.Input,
            slot.AuditLevel,
            runtime!.Status,
            runtime.NextRunAtUtc,
            slotSchedules
        );

        await ScheduleTestOps.RegisterAsync(Services, [command], ct);
    }
}
