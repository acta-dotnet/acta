// Concept: a child group never throws on its own; ThrowIfAnyFailed is the opt-in escalation.
// A failed child surfaces in ChildrenWaitOutcome.Failed and MapOutcome.Failed.
using Acta;
using Acta.Concepts.ChildFailureOutcomes;
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
Console.WriteLine($"parent: {mapOutcome.Status}, report: {mapOutcome.Value!.Summary}");

// --- Scenario 2: WaitChildrenAsync, ThrowIfAnyFailed escalation ---
// The handler calls ThrowIfAnyFailed after a child fails; ChildGroupException fails the parent at once,
// since a retry would find the same failed child. The driver reads the Failed status without the
// exception propagating here.
Console.WriteLine("--- scenario 2: children with one failing, ThrowIfAnyFailed ---");
var joinOutcome = await jobs.CallAsync<RunJoinEscalated>(new RunJoinEscalated());
Console.WriteLine($"parent: {joinOutcome.Status} (expected Failed)");

await host.StopAsync();

namespace Acta.Concepts.ChildFailureOutcomes
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

            return new MapReport($"{succeeded}/{ids.Length} items succeeded");
        }

        // MaxAttempts = 1 so the job fails immediately without retrying; the parent sees terminal fast.
        [Job("process-item", MaxAttempts = 1)]
        public async Task ProcessItemHandler(ProcessItem input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            if (input.ShouldFail)
            {
                Console.WriteLine($"  item {input.Id}: failing");
                throw new InvalidOperationException($"item {input.Id} failed intentionally");
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

            // ThrowIfAnyFailed throws ChildGroupException; the parent lands Failed.
            result.ThrowIfAnyFailed();
        }

        // MaxAttempts = 1 so the failing child terminates immediately.
        [Job("run-child", MaxAttempts = 1)]
        public async Task RunChildHandler(RunChild input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            if (input.ShouldFail)
            {
                Console.WriteLine($"  child {input.Name}: failing");
                throw new InvalidOperationException($"child {input.Name} failed intentionally");
            }
            Console.WriteLine($"  child {input.Name}: done");
        }
    }
}
