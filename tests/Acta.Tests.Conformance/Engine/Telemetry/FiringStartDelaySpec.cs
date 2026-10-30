using System.Diagnostics.Metrics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Telemetry;

/// <summary>
/// Sleeps once per firing, so a firing takes two attempts: the one its admission readied and the continuation. The
/// timer is named by the firing, since a slot keeps its consumed timers.
/// </summary>
internal static class NapHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) =>
        ctx.SleepAsync($"nap-{ctx.FiringId:N}", TimeSpan.FromMinutes(10), ct: ct);
}

/// <summary>One Permanent definition on an every-minute cron, whose handler sleeps once per firing.</summary>
public sealed class FiringStartDelayManifest : IJobManifest
{
    public const string JobName = "nap-slot";
    public const string ScheduleName = "every-minute";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: JobName,
                HandlerType: typeof(NapHandler),
                MethodName: nameof(NapHandler.Run),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 1,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.None,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await NapHandler.Run(ctx, ct);
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
                        Expression: "* * * * *",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Cron,
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
/// <c>acta.firing.start.delay</c> is one sample per firing: the attempt the admission readied records it, a
/// continuation after a sleep records nothing, and the slot's next firing records again.
/// </summary>
[ConformanceSpec(
    "telemetry.firing-start-delay",
    "A firing's start delay is recorded once, on the attempt its admission readied",
    Area = "Telemetry",
    Contract = "acta.firing.start.delay records one sample per firing, on the attempt its admission readied and not on a continuation."
)]
public sealed class FiringStartDelaySpec : ActaRuntimeTestBase<ConformanceFixture, FiringStartDelayManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Clock = new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(
        DisplayName = "The admitted attempt records the delay, the continuation after a sleep does not, and the next firing records again"
    )]
    public async Task One_sample_per_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, FiringStartDelayManifest.JobName, ct);
        var samples = 0;
        using var listener = FiringStartDelayListener(() => samples++);

        await AdmitAsync(slotId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(1, samples);

        // A continuation's next run follows the admission's stamp, as a completion writes it; the timer is due with it.
        var admitted = (await ReadFiringCheckpointAsync(slotId, ct)).ModifiedAtUtc;
        var continued = admitted.AddMilliseconds(1);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", continued),
            ("@p_id", slotId)
        );
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due WHERE job_id = @p_id AND checkpoint_kind_code = 30",
            ct,
            ("@p_due", continued),
            ("@p_id", slotId)
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(1, samples);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        await AdmitAsync(slotId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(2, samples);
    }

    /// <summary>Moves the clock past the cursor and admits the firing, which readies the slot.</summary>
    private async Task AdmitAsync(long slotId, CancellationToken ct)
    {
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.NotNull(schedule.NextOccurrenceAtUtc);
        Clock.AdvanceTo(schedule.NextOccurrenceAtUtc.Value.AddSeconds(30));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }

    private async Task<CheckpointEntity> ReadFiringCheckpointAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(checkpoint);
        return checkpoint;
    }

    /// <summary>Counts this host's <c>acta.firing.start.delay</c> measurements; parallel hosts publish the same instrument names, so the meter is matched by reference.</summary>
    private MeterListener FiringStartDelayListener(Action onMeasurement)
    {
        var meter = Services.GetRequiredService<ActaMetrics>().Meter;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "acta.firing.start.delay")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => onMeasurement());
        listener.Start();
        return listener;
    }
}
