using Microsoft.Extensions.DependencyInjection;

namespace Acta.Verify;

internal static class SchedulingCases
{
    public static async Task<CaseResult> RunAsync(
        RunPlan plan,
        Scenario scenario,
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
        if (scenario.Id == "rate-meters")
        {
            var definition =
                await operations.Definitions.GetAsync("verify", "work", ct)
                ?? throw new InvalidOperationException("Missing workload definition.");
            await operations.Definitions.UpdateOverridesAsync("verify", "work", definition.Version, new(RateLimit: "10/s"), ct: ct);
        }
        if (scenario.Id == "priority-tenant-contention")
        {
            await operations.Tenants.RegisterAsync("one", "Tenant one", ct: ct);
            await operations.Tenants.RegisterAsync("two", "Tenant two", ct: ct);
        }
        // rate-meters offers more jobs than its widest window's budget, so a meter that over-admits fails every window.
        var count =
            scenario.Id == "lane-order-many" ? 2000
            : scenario.Id == "rate-meters" ? 150
            : 30;
        var input = Enumerable
            .Range(0, count)
            .Select(i => new WorkInput(
                $"root-{i}",
                WorkMs: scenario.Id == "lane-order-hot" && i == 0 ? 250 : 10,
                Failures: scenario.Id == "lane-order-hot" && i == 0 ? 1 : 0,
                Witness: true
            ))
            .ToArray();
        var admitted = new List<(WorkInput Work, EnqueueJobResponse Job, string? Lane)>();
        for (var i = 0; i < input.Length; i++)
        {
            var lane =
                scenario.Id == "lane-order-many" ? $"lane-{i % 1000}"
                : scenario.Id == "lane-order-hot" ? "hot"
                : null;
            var request = new EnqueueJobRequest(
                "verify",
                "work",
                JobPayload.Json(input[i], WorkJson.Default.WorkInput),
                DeduplicationKey: input[i].Key,
                Lane: lane,
                ConcurrencyKey: scenario.Id == "concurrency-key-limit" ? "hot" : null,
                Priority: scenario.Id == "priority-tenant-contention" && i % 2 == 0 ? JobPriorityCode.High : JobPriorityCode.Normal,
                TenantName: scenario.Id == "priority-tenant-contention"
                    ? i % 3 == 0
                        ? "two"
                        : "one"
                    : null
            );
            admitted.Add((input[i], await jobs.EnqueueAsync(request, ct), lane));
        }
        using var workers = new OwnedWorkerPool();
        await workers.StartAsync(
            state,
            null,
            scenario.Id == "rate-meters" ? 2 : 1,
            scenario.Id == "priority-tenant-contention" ? 1 : null,
            ct
        );
        var reader = new DurableReader(state);
        await plan.DrainAsync(async () => (await reader.UnfinishedRootsAsync(0, ct)).Count, plan.Deadline(count, 0, state.DrainRate), ct);
        var actual = await reader.OutcomesAsync(ct);
        var checks = OutcomeCheck.Reconcile(WorkloadModel.Expected(input), actual).ToList();
        var notes = await NotesAsync(operations, ct);
        var entries = notes.Where(n => n.Message?.StartsWith("begin ", StringComparison.Ordinal) == true).OrderBy(n => n.EventId).ToArray();
        checks.Add(
            new(
                "witness-coverage",
                scenario.Id,
                input.All(i => entries.Any(e => e.Message == "begin " + i.Key)),
                "Every root left an independent durable entry witness."
            )
        );
        if (scenario.Id is "lane-order-many" or "lane-order-hot")
        {
            // Note ids are assigned in insert order, and a member's end note commits before the completion that
            // promotes the next member, so the ids order the two notes without a clock.
            var firstBegin = entries.GroupBy(e => e.EntityId).ToDictionary(g => g.Key, g => g.Min(e => e.EventId));
            var lastEnd = notes
                .Where(n => n.Message?.StartsWith("end ", StringComparison.Ordinal) == true)
                .GroupBy(n => n.EntityId)
                .ToDictionary(g => g.Key, g => g.Max(n => n.EventId));
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
        if (scenario.Id == "concurrency-key-limit")
        {
            var concurrency = 0;
            var maximum = 0;
            foreach (var note in notes.OrderBy(e => e.EventId))
            {
                concurrency += note.Message?.StartsWith("begin ", StringComparison.Ordinal) == true ? 1 : -1;
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
        if (scenario.Id == "priority-tenant-contention")
        {
            var starts = entries
                .Select(e => int.Parse(e.Message!.AsSpan("begin root-".Length), System.Globalization.CultureInfo.InvariantCulture))
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
        if (scenario.Id == "rate-meters")
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
        bundle.Write($"histories/{tag}-checks.json", checks.ToArray(), VerifyJson.Default.CheckArray);
        var passed = checks.All(c => c.Passed);
        if (!passed)
        {
            bundle.Write($"histories/{tag}-notes.json", notes, WorkJson.Default.ListEventsItemArray);
        }
        return new(
            scenario.Id,
            environment.Provider,
            "verify",
            0,
            passed ? "PASS" : "FAIL",
            scenario.Id == "priority-tenant-contention" ? "Finite strict-priority/tenant contention; no starvation-freedom claim." : null,
            passed ? "PASS" : "FAIL",
            passed ? "PASS" : "UNVERIFIED",
            "COMPLETE",
            "NOT_MEASURED",
            [],
            [
                $"histories/{tag}-checks.json",
                .. bundle.WriteRows(tag, passed, WorkloadModel.Expected(input), actual),
                .. passed ? Array.Empty<string>() : [$"histories/{tag}-notes.json"],
            ],
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
                new(JobNamespace: "verify", EventCode: EventCode.JobNoteRecorded, PageSize: 100, Cursor: cursor),
                ct
            );
            notes.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return [.. notes];
    }
}
