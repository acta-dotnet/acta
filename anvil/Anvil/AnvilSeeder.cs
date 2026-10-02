using Acta;

namespace Anvil;

/// <summary>
/// Streams a named Anvil workload into the current namespace. Requests are generated lazily and written
/// in bounded chunks, so even the one-million-job no-op run begins executing immediately without building
/// a one-million-element request list.
/// </summary>
public sealed class AnvilSeeder(IJobs jobs, AnvilSession session)
{
    private const int ChunkSize = 5_000;
    private const int FanOutChildCount = 5;

    // The metered and laned slices' ceilings, and the lane depth; see CrashRecoveryPlan.
    private const int MeteredCap = 10_000;
    private const int LanedCap = 20_000;
    private const int LaneDepth = 10;
    private const int LaneSteps = 3;

    private readonly IJobs _jobs = jobs;
    private readonly AnvilSession _session = session;

    // Delay is per item so a line can be spread across a window rather than all due at once.
    private sealed record SeedLine(
        string JobName,
        int Count,
        bool Fails,
        Func<int, JobPayload> Payload,
        Func<int, int?>? Delay = null,
        Func<int, string>? Lane = null,
        JobPriorityCode? Priority = null
    );

    private static IReadOnlyList<SeedLine> Plan(AnvilRunSpec spec) =>
        spec.Workload switch
        {
            AnvilWorkloadCode.NoOp => [new("noop", spec.Load, false, i => AnvilPayloads.Json(new NoOp($"noop-{i}")))],
            AnvilWorkloadCode.Steady =>
            [
                new("steady-success", spec.Load, false, i => AnvilPayloads.Json(new SteadySuccess($"steady-{i}", 25))),
            ],
            AnvilWorkloadCode.CrashRecovery => CrashRecoveryPlan(spec),
            AnvilWorkloadCode.RetryAndFailure => RetryAndFailurePlan(spec.Load),
            AnvilWorkloadCode.FanOut =>
            [
                new("fan-out", spec.Load, false, i => AnvilPayloads.Json(new FanOut($"fan-out-{i}", FanOutChildCount))),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Workload, "Unknown Anvil workload."),
        };

    // A tenth of the crash workload is the at-most-once charge shape, so the same kills that reclaim
    // slow-success land inside a body that must never run twice. Marked as expected-to-fail: an
    // interrupted AtMostOnce step terminalizes the ambiguity rather than retrying, which is the
    // contract, so those failures are the shape working and the board's target must include them.
    //
    // Another tenth is the metered shape, capped at ten thousand, and both slices come out of
    // slow-success so the workload keeps its requested size. Metered jobs all come due at once inside
    // the chaos window, and the meter then decides when each one starts, so admissions are handed out
    // at full rate while workers are dying. The cap is what keeps a million-job run a run of the ledger
    // rather than of the meter: its tenth would be 100,000 turns at ten a second, and the ledger would
    // sit drained for the last two and a half hours while the meter paid them out. The slice is split
    // across three meters, a fifth at ten a second and two fifths each at fifty and at a hundred, so a
    // million-job run pays its ten thousand out in minutes and the verdict checks three contracts side
    // by side.
    //
    // A further tenth, capped at twenty thousand, is laned: ten jobs to a lane, due inside the chaos
    // window like the charges, a lane at a time, so a lane's head is running when its worker dies. Lane
    // membership is the index modulo the lane count, and the plan seeds in index order, so each lane's
    // members are enqueued, and numbered, in the order the lane must run them.
    private static IReadOnlyList<SeedLine> CrashRecoveryPlan(AnvilRunSpec spec)
    {
        var charges = Math.Max(1, spec.Load / 10);
        var metered = Math.Min(Math.Max(3, spec.Load / 10), MeteredCap);
        var laned = Math.Min(Math.Max(LaneDepth, spec.Load / 10), LanedCap);
        var lanes = Math.Max(1, laned / LaneDepth);
        var slow = Math.Max(0, spec.Load - charges - metered - laned);
        var spread = Math.Max(1, spec.EffectSpreadSeconds);
        int? DueInChaos(int i) => spec.EffectDelaySeconds <= 0 ? null : spec.EffectDelaySeconds + (i % spread);
        // The meters' slice is due all at once, so each meter runs at its full rate while workers die: spread
        // like the charges, it would trickle in below the rate and leave the contract unloaded.
        int? DueTogetherInChaos(int _) => spec.EffectDelaySeconds <= 0 ? null : spec.EffectDelaySeconds;
        var meterShares = new[] { metered / 5, metered * 2 / 5, metered - (metered / 5) - (metered * 2 / 5) };
        return
        [
            new("slow-success", slow, false, i => AnvilPayloads.Json(new SlowSuccess($"slow-{i}", 5, spec.StepDelayMs))),
            .. MeteredJob.Meters.Select(
                (meter, m) =>
                    new SeedLine(
                        meter.JobName,
                        meterShares[m],
                        false,
                        i => meter.Payload($"{meter.JobName}-{i}"),
                        DueTogetherInChaos,
                        Priority: JobPriorityCode.High
                    )
            ),
            new(
                "laned",
                laned,
                false,
                i => AnvilPayloads.Json(new Laned($"laned-{i}", LaneSteps, spec.StepDelayMs)),
                // Every member of a lane shares the lane's due instant, so the lane starts at once and
                // runs through the window one member at a time.
                i => DueInChaos(i % lanes),
                i => $"lane-{i % lanes}",
                // High, so a lane's head is claimed the moment it is due rather than behind the slow backlog
                // seeded earlier in the same band, which drains only after the chaos has ended. The charges and
                // the meters are High for the same reason.
                JobPriorityCode.High
            ),
            new(
                "at-most-once-charge",
                charges,
                true,
                i => AnvilPayloads.Json(new AtMostOnceCharge($"charge-{i}", spec.StepDelayMs * 2)),
                // Due inside the window where a kill can actually interrupt a body: after the warm-up,
                // because nothing is reclaimable before a lease can lapse, and before the chaos ends.
                // Zero means due now, which is what the cockpit wants and a certification never does.
                DueInChaos,
                // High for the reason the laned slice is: in the slow backlog's band it would be claimed
                // only once that backlog drained, after the chaos had ended.
                Priority: JobPriorityCode.High
            ),
        ];
    }

    private static IReadOnlyList<SeedLine> RetryAndFailurePlan(int load)
    {
        var failureCount = Math.Max(1, load / 20);
        var flakyCount = load - failureCount;
        return
        [
            new("flaky-once", flakyCount, false, i => AnvilPayloads.Json(new FlakyOnce($"flaky-{i}"))),
            new("always-fails", failureCount, true, i => AnvilPayloads.Json(new AlwaysFails($"doomed-{i}"))),
        ];
    }

    public async ValueTask SeedAsync(int batch, AnvilRunSpec spec, SeedProgress progress, CancellationToken ct = default)
    {
        var plan = Plan(spec);
        progress.Begin(plan.Sum(line => line.Count));

        try
        {
            // Return the action response before any database insertion while keeping Begin owned here.
            await Task.Yield();

            foreach (var line in plan)
            {
                var chunk = new List<JobEnqueueRequest>(ChunkSize);
                for (var i = 0; i < line.Count; i++)
                {
                    chunk.Add(
                        Request(
                            _session.NamespaceName,
                            _session.RunId,
                            batch,
                            line.JobName,
                            i,
                            line.Payload(i),
                            spec.Workload,
                            line.Delay?.Invoke(i),
                            line.Lane?.Invoke(i),
                            line.Priority
                        )
                    );

                    if (chunk.Count == ChunkSize)
                    {
                        await FlushChunkAsync(chunk, line.Fails, progress, ct);
                        chunk = new List<JobEnqueueRequest>(ChunkSize);
                    }
                }

                if (chunk.Count > 0)
                {
                    await FlushChunkAsync(chunk, line.Fails, progress, ct);
                }
            }

            progress.Complete();
        }
        catch (Exception ex)
        {
            progress.Fail(FirstLine(ex.Message));
            throw;
        }
    }

    private async Task FlushChunkAsync(
        IReadOnlyList<JobEnqueueRequest> chunk,
        bool expectedToFail,
        SeedProgress progress,
        CancellationToken ct
    )
    {
        var outcomes = await _jobs.EnqueueBatchAsync(chunk, ct);
        var inserted = outcomes.Count(outcome => outcome.Action == JobEnqueueAction.Inserted);
        progress.Advance(inserted, outcomes.Count - inserted);
        if (expectedToFail)
        {
            _session.AddExpectedFailures(inserted);
        }
    }

    internal static JobEnqueueRequest Request(
        string namespaceName,
        string runId,
        int batch,
        string jobName,
        int index,
        JobPayload input,
        AnvilWorkloadCode workload,
        int? delaySeconds = null,
        string? lane = null,
        JobPriorityCode? priority = null
    ) =>
        new(
            namespaceName,
            jobName,
            input,
            DeduplicationKey: $"anvil/{runId}/{batch:000}/{jobName}/{index}",
            CorrelationKey: runId,
            DelaySeconds: delaySeconds,
            Lane: lane,
            Priority: priority,
            Tags: [new TagInput("demo", "anvil"), new TagInput("run", runId), new TagInput("workload", workload.ToString())],
            // Every sixth of the seeded jobs cycles through a demo tenant so tenant-scoped views have
            // data; the rest stay untenanted so both kinds of jobs exist side by side.
            TenantKey: index % 6 < AnvilTenants.All.Length ? AnvilTenants.All[index % 6].Key : null
        );

    private static string FirstLine(string value)
    {
        var line = value.Split('\n', '\r')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }
}
