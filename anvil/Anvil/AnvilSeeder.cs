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

    private readonly IJobs _jobs = jobs;
    private readonly AnvilSession _session = session;

    private sealed record SeedLine(string JobName, int Count, bool Fails, Func<int, JobPayload> Payload);

    private static IReadOnlyList<SeedLine> Plan(AnvilRunSpec spec) =>
        spec.Workload switch
        {
            AnvilWorkloadCode.NoOp => [new("noop", spec.Load, false, i => AnvilPayloads.Json(new NoOp($"noop-{i}")))],
            AnvilWorkloadCode.Steady =>
            [
                new("steady-success", spec.Load, false, i => AnvilPayloads.Json(new SteadySuccess($"steady-{i}", 25))),
            ],
            AnvilWorkloadCode.CrashRecovery =>
            [
                new("slow-success", spec.Load, false, i => AnvilPayloads.Json(new SlowSuccess($"slow-{i}", 5, spec.StepDelayMs))),
            ],
            AnvilWorkloadCode.RetryAndFailure => RetryAndFailurePlan(spec.Load),
            AnvilWorkloadCode.FanOut =>
            [
                new("fan-out", spec.Load, false, i => AnvilPayloads.Json(new FanOut($"fan-out-{i}", FanOutChildCount))),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Workload, "Unknown Anvil workload."),
        };

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
                    chunk.Add(Request(_session.NamespaceName, _session.RunId, batch, line.JobName, i, line.Payload(i), spec.Workload));

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
        AnvilWorkloadCode workload
    ) =>
        new(
            namespaceName,
            jobName,
            input,
            DeduplicationKey: $"anvil/{runId}/{batch:000}/{jobName}/{index}",
            CorrelationKey: runId,
            DelaySeconds: null,
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
