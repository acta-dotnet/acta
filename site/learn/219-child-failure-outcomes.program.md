# The whole program: handle child jobs that fail

The how-to is [/learn/219-child-failure-outcomes.md](/learn/219-child-failure-outcomes.md). This file holds every source file of `learn/200-durable-execution/219-child-failure-outcomes` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### child-failure-outcomes.cs

```csharp
// How to handle child jobs that fail
// A group of children never throws by itself. A failed child shows in the `Failed` list of the wait or the map, and
// `ThrowIfAnyFailed` is how the parent chooses to fail with it.
using Acta;
using Acta.Learn.ChildFailureOutcomes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ChildFailureOutcomesJobs>("child-failure-outcomes");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// --- Scenario 1: MapAsync, soft handling ---
// The handler inspects MapOutcome.Failed and returns a partial result; the parent lands Succeeded.
Console.WriteLine("--- scenario 1: map with one failing item, soft handling ---");
var mapOutcome = await jobs.CallAsync(ChildFailureOutcomesJobs.RunMapSoft());
Console.WriteLine($"parent: {mapOutcome.Status}, report: {mapOutcome.ValueOrThrow().Summary}");

// --- Scenario 2: WaitChildrenAsync, ThrowIfAnyFailed escalation ---
// The handler calls ThrowIfAnyFailed after a child fails; ChildGroupException fails the parent at once,
// since a retry would find the same failed child. The driver reads the Failed status without the
// exception propagating here.
Console.WriteLine("--- scenario 2: children with one failing, ThrowIfAnyFailed ---");
var joinOutcome = await jobs.CallAsync<RunJoinEscalated>(new RunJoinEscalated());
Console.WriteLine($"parent: {joinOutcome.Status} (expected Failed)");

await host.StopAsync();

namespace Acta.Learn.ChildFailureOutcomes
{
    // ---- scenario 1 inputs and results ----

    public readonly record struct RunMapSoft;

    public sealed record ProcessItem(string Id, bool ShouldFail);

    public sealed record MapReport(string Summary);

    // ---- scenario 2 inputs ----

    public readonly record struct RunJoinEscalated;

    public sealed record RunChild(string Name, bool ShouldFail);

    public sealed class FailureScenarioJobs
    {
        // Scenario 1: map over three items; one item is rigged to fail. After waiting, the handler
        // checks MapOutcome.Failed and returns a partial summary; the parent lands Succeeded.
        [Job("run-map-soft")]
        public async Task<MapReport> HandleMapSoft(RunMapSoft _, JobContext context, CancellationToken ct)
        {
            string[] ids = ["item-a", "item-b", "item-c"];

            // learn: 1. Carry on without the items that failed
            // The map does not throw for a failed item. `Failed` lists them, and this parent returns a partial result
            // and lands Succeeded.
            var result = await context.MapAsync(
                "process",
                ids,
                itemKey: id => id,
                child: id => new ProcessItem(id, ShouldFail: id == "item-b"),
                ct
            );

            var succeeded = result.Items.Count(i => i.Outcome.Succeeded);

            foreach (var f in result.Failed)
            {
                Console.WriteLine($"  item {f.Key} failed (status={f.Outcome.Status}); continuing without it");
            }
            // learn: end

            return new MapReport($"{succeeded}/{ids.Length} items succeeded");
        }

        // FailAsync ends the job Failed at once, with no retry, and leaves MaxAttempts to absorb crashes; a thrown
        // exception would retry, and MaxAttempts = 1 would fail the job on its first crash too.
        [Job("process-item")]
        public async Task ProcessItemHandler(ProcessItem input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            if (input.ShouldFail)
            {
                Console.WriteLine($"  item {input.Id}: failing");
                await context.FailAsync($"item {input.Id} failed intentionally", ct);
            }
            Console.WriteLine($"  item {input.Id}: done");
        }

        // Scenario 2: two children enqueued and waited for; one child fails. The handler calls
        // ThrowIfAnyFailed on the outcome, and the parent lands Failed at once, with no retry.
        [Job("run-join-escalated")]
        public async Task HandleJoinEscalated(RunJoinEscalated _, JobContext context, CancellationToken ct)
        {
            var alpha = await context.EnqueueChildAsync("alpha", new RunChild("alpha", ShouldFail: false), ct);
            var beta = await context.EnqueueChildAsync("beta", new RunChild("beta", ShouldFail: true), ct);

            var result = await context.WaitChildrenAsync([alpha, beta], ct);

            foreach (var child in result.Failed)
            {
                Console.WriteLine($"  child {child.JobRef} failed (status={child.Status})");
            }

            // learn: 2. Or fail the parent with its failed child
            // ThrowIfAnyFailed throws ChildGroupException; the parent lands Failed.
            result.ThrowIfAnyFailed();
            // learn: end
        }

        // FailAsync, as in process-item: the failing child ends at once and keeps its budget for crashes.
        [Job("run-child")]
        public async Task RunChildHandler(RunChild input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            if (input.ShouldFail)
            {
                Console.WriteLine($"  child {input.Name}: failing");
                await context.FailAsync($"child {input.Name} failed intentionally", ct);
            }
            Console.WriteLine($"  child {input.Name}: done");
        }
    }
}
```

