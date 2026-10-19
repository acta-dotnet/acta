using Microsoft.Extensions.DependencyInjection;

namespace Acta.Bench;

internal static class SchedulingCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        ScenarioDescriptor scenario,
        OwnedProvider environment,
        EvidenceBundle bundle,
        CancellationToken ct
    )
    {
        var tag = $"{environment.Provider}-{scenario.Id}-scheduling";
        var state = environment.Trial(bundle.Resolve($"repro/state/{tag}"));
        using var host = await RuntimeHost.StartAsync(state.Provider, state.ConnectionString, state.Schema, false, true, ct);
        var jobs = host.Services.GetRequiredService<IJobs>();
        var operations = host.Services.GetRequiredService<IActaOperations>();
        if (scenario.Id == "O05")
        {
            var definition =
                await operations.Definitions.GetAsync("bench", "work", ct)
                ?? throw new InvalidOperationException("Missing workload definition.");
            await operations.Definitions.UpdateOverridesAsync("bench", "work", definition.Version, new(RateLimit: "10/s"), ct: ct);
        }
        if (scenario.Id == "O04")
        {
            await operations.Tenants.RegisterAsync("one", "Tenant one", ct: ct);
            await operations.Tenants.RegisterAsync("two", "Tenant two", ct: ct);
        }
        // O05 offers more roots than its widest window's budget, so a meter that over-admits fails every window.
        var count =
            scenario.Id == "O01" ? 2000
            : scenario.Id == "O05" ? 150
            : 30;
        var input = Enumerable
            .Range(0, count)
            .Select(i => new BenchWork(
                $"root-{i}",
                WorkMs: scenario.Id == "O02" && i == 0 ? 250 : 10,
                Failures: scenario.Id == "O02" && i == 0 ? 1 : 0,
                Witness: true
            ))
            .ToArray();
        bundle.Write($"histories/{tag}-expected.json", WorkloadModel.Expected(input), BenchJson.Default.ExpectedOutcomeArray);
        var admitted = new List<(BenchWork Work, EnqueueJobResponse Job, string? Lane)>();
        for (var i = 0; i < input.Length; i++)
        {
            var lane =
                scenario.Id == "O01" ? $"lane-{i % 1000}"
                : scenario.Id == "O02" ? "hot"
                : null;
            var request = new EnqueueJobRequest(
                "bench",
                "work",
                JobPayload.Json(input[i], WorkJson.Default.BenchWork),
                DeduplicationKey: input[i].Key,
                Lane: lane,
                ConcurrencyKey: scenario.Id == "O03" ? "hot" : null,
                Priority: scenario.Id == "O04" && i % 2 == 0 ? JobPriorityCode.High : JobPriorityCode.Normal,
                TenantName: scenario.Id == "O04"
                    ? i % 3 == 0
                        ? "two"
                        : "one"
                    : null
            );
            admitted.Add((input[i], await jobs.EnqueueAsync(request, ct), lane));
        }
        using var workers = new OwnedWorkerPool();
        await workers.StartAsync(state, null, scenario.Id == "O05" ? 2 : 1, scenario.Id == "O04" ? 1 : null, ct);
        var reader = new DurableReader(state);
        await plan.DrainAsync(
            async () => count - (await reader.ReadAsync(null, ct)).Count(r => r.Status == 100),
            plan.Deadline(count, 0, state.DrainRate),
            ct
        );
        var actual = await reader.OutcomesAsync(ct);
        var checks = OutcomeOracle.Reconcile(WorkloadModel.Expected(input), actual).ToList();
        var notes = await NotesAsync(operations, ct);
        var entries = notes
            .Where(n => n.ReasonMessage?.StartsWith("begin ", StringComparison.Ordinal) == true)
            .OrderBy(n => n.JobEventId)
            .ToArray();
        checks.Add(
            new(
                "witness-coverage",
                scenario.Id,
                input.All(i => entries.Any(e => e.ReasonMessage == "begin " + i.Key)),
                "Every root left an independent durable entry witness."
            )
        );
        if (scenario.Id is "O01" or "O02")
        {
            // Note ids are assigned in insert order, and a member's end note commits before the completion that
            // promotes the next member, so the ids order the two notes without a clock.
            var firstBegin = entries.GroupBy(e => e.JobId!.Value).ToDictionary(g => g.Key, g => g.Min(e => e.JobEventId));
            var lastEnd = notes
                .Where(n => n.ReasonMessage?.StartsWith("end ", StringComparison.Ordinal) == true)
                .GroupBy(n => n.JobId!.Value)
                .ToDictionary(g => g.Key, g => g.Max(n => n.JobEventId));
            foreach (var lane in admitted.GroupBy(a => a.Lane, StringComparer.Ordinal))
            {
                var members = lane.Select(a => a.Job.JobId).Order().ToArray();
                checks.Add(
                    new(
                        "lane-order",
                        lane.Key!,
                        members
                            .Zip(members.Skip(1))
                            .All(p => firstBegin.GetValueOrDefault(p.Second) > lastEnd.GetValueOrDefault(p.First, long.MaxValue)),
                        "Each lane member first starts after the previous member last finished, including the retrying head."
                    )
                );
            }
        }
        if (scenario.Id == "O03")
        {
            var concurrency = 0;
            var maximum = 0;
            foreach (var note in notes.OrderBy(e => e.JobEventId))
            {
                concurrency += note.ReasonMessage?.StartsWith("begin ", StringComparison.Ordinal) == true ? 1 : -1;
                maximum = Math.Max(maximum, concurrency);
            }
            checks.Add(
                new(
                    "key-concurrency",
                    "hot",
                    maximum == 1 && concurrency == 0,
                    "Durable handler entry/exit witnesses do not overlap under the declared one-slot key."
                )
            );
        }
        if (scenario.Id == "O04")
        {
            var starts = entries
                .Select(e => int.Parse(e.ReasonMessage!.AsSpan("begin root-".Length), System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var firstNormal = Array.FindIndex(starts, i => i % 2 != 0);
            checks.Add(
                new(
                    "strict-priority",
                    "finite-cohort",
                    firstNormal == count / 2 && starts.Take(firstNormal).All(i => i % 2 == 0),
                    "With one executor and all jobs due before startup, every high-priority job starts before normal-priority work, across tenant scopes."
                )
            );
            for (var i = 0; i < admitted.Count; i++)
            {
                var stored = await jobs.GetAsync(admitted[i].Job, ct);
                checks.Add(
                    new(
                        "tenant-scope-preserved",
                        input[i].Key,
                        stored?.TenantName == (i % 3 == 0 ? "two" : "one"),
                        "Tenant identity remains attached to its exact logical root."
                    )
                );
            }
        }
        if (scenario.Id == "O05")
        {
            var times = entries.Select(e => e.CreatedAtUtc).Order().ToArray();
            foreach (var window in new[] { 1, 5, 10 })
            {
                // At 10/s with one second of burst and one second of turn validity, T seconds admit 10 * (T + 1) + 10.
                var budget = 10 * (window + 1) + 10;
                var maximum = times.Max(t => times.Count(u => u >= t && u < t.AddSeconds(window)));
                checks.Add(
                    new(
                        "rate-envelope",
                        $"{window}s",
                        maximum <= budget,
                        $"{maximum} starts in the busiest {window}s window; the envelope admits {budget}."
                    )
                );
            }
        }
        bundle.Write($"histories/{tag}-notes.json", notes, WorkJson.Default.ListEventsItemArray);
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), BenchJson.Default.CheckArray);
        bundle.Write($"histories/{tag}-observed.json", actual, BenchJson.Default.ObservedOutcomeArray);
        var passed = checks.All(c => c.Passed);
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            0,
            passed ? "PASS" : "FAIL",
            scenario.Id == "O04" ? "Finite strict-priority/tenant contention; no starvation-freedom claim." : null,
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [$"histories/{tag}-notes.json", $"histories/{tag}-checks.json"],
            Instrumented: true
        );
    }

    private static async Task<ListEventsItem[]> NotesAsync(IActaOperations operations, CancellationToken ct)
    {
        List<ListEventsItem> notes = [];
        string? cursor = null;
        do
        {
            var page = await operations.Ledger.ListEventsAsync(
                new(JobNamespace: "bench", EventCode: EventCode.JobNoteRecorded, PageSize: 100, Cursor: cursor),
                ct
            );
            notes.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return [.. notes];
    }
}
