using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

internal static class ReplyLossCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct,
        int repetition = 1
    )
    {
        var tag = $"{environment.Provider}-{scenario.Id}-reply-loss-{repetition}";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var oracle = new EffectService(bundle.Resolve($"repro/state/{tag}/oracle.db"));
        await oracle.StartAsync(ct);
        var enqueueLoss = scenario.Id == "F07";
        var injection = new StoreInjection(enqueueLoss ? "EnqueueJob" : "CompleteExecutions", "committed-reply", true);
        using var host = await RuntimeHost.StartAsync(
            state.Provider,
            state.ConnectionString,
            state.Schema,
            false,
            true,
            ct,
            enqueueLoss ? injection : null,
            oracle
        );
        var jobs = host.Services.GetRequiredService<IJobs>();
        var input = new BenchWork("root");
        var expected = WorkloadModel.Expected([input]);
        var request = new EnqueueJobRequest(
            "bench",
            "work",
            JobPayload.Json(input, WorkJson.Default.BenchWork),
            DeduplicationKey: input.Key
        );
        var submission = jobs.EnqueueAsync(request, ct).AsTask();
        // The worker starts after the enqueue, so the completion injection can name the case's job.
        using var worker = OwnedWorker.Start(state, oracle, enqueueLoss ? null : injection with { JobId = (await submission).JobId });
        await worker.ReadyAsync(ct);
        var boundary = await oracle.WaitBarrierAsync("committed-reply", TimeSpan.FromSeconds(30), ct);
        var reader = new DurableReader(state);
        var before = await reader.ReadAsync(null, ct);
        List<Check> checks =
        [
            new(
                "commit-before-lost-reply",
                input.Key,
                boundary is not null
                    && before.Any(r =>
                        r.Key == input.Key
                        && r.ParentJobId is null
                        && r.JobNamespace == "bench"
                        && (enqueueLoss || r.Status == 100 && r.Output == expected[0].Output)
                    ),
                "Independent SQL reader saw the committed record while its caller was held before receiving the result."
            ),
        ];
        await oracle.ReleaseAsync(ct);
        var ambiguous = false;
        try
        {
            await submission;
        }
        catch (TimeoutException)
        {
            ambiguous = true;
        }
        if (enqueueLoss)
        {
            checks.Add(
                new("producer-ambiguity", input.Key, ambiguous, "Producer observed a lost reply rather than an invented rejection.")
            );
            var retried = await jobs.EnqueueAsync(request, ct);
            checks.Add(
                new(
                    "deduplicated-retry",
                    input.Key,
                    retried.Action == EnqueueJobAction.Deduplicated && before.Any(r => r.Key == input.Key && r.JobId == retried.JobId),
                    "Producer retried the same business key."
                )
            );
        }
        await FaultCaseRunner.WaitAsync(
            async () => (await reader.ReadAsync(null, ct)).All(r => r.Status == 100),
            plan.Deadline(1, worker.Settings!.LeaseSeconds, state.DrainRate),
            ct
        );
        var actual = await reader.OutcomesAsync(ct);
        checks.Add(
            new(
                "single-authoritative-attempt",
                input.Key,
                (await reader.ReadAsync(null, ct)).All(r => r.ExecutionNumber == 1),
                "The committed terminal outcome survived the lost reply without creating another authoritative attempt."
            )
        );
        checks.AddRange(OutcomeOracle.Reconcile(expected, actual));
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
        var complete = boundary is not null;
        var passed = checks.All(c => c.Passed);
        if (!(complete && passed))
        {
            bundle.Write($"histories/{tag}-before-reply.json", before.ToArray(), TrialJson.Default.DurableRowArray);
        }
        return new(
            scenario.Id,
            environment.Provider,
            "fault",
            repetition,
            !complete ? "INCONCLUSIVE"
                : passed ? "PASS"
                : "FAIL",
            !complete ? "Required committed-response boundary was not hit."
                : passed ? null
                : "Reply-loss verification failed.",
            !complete ? "UNVERIFIED"
                : passed ? "PASS"
                : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            complete ? "COMPLETE" : "INCOMPLETE",
            "NOT_MEASURED",
            [],
            [
                $"histories/{tag}-checks.json",
                .. bundle.WriteRows(tag, complete && passed, expected, actual),
                .. complete && passed ? Array.Empty<string>() : [$"histories/{tag}-before-reply.json"],
            ],
            Instrumented: true
        );
    }
}
