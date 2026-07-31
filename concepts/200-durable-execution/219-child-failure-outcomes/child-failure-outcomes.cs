// Concept: a child group never throws on its own; ThrowIfAnyFailed is the opt-in escalation.
// A failed child surfaces in JoinOutcome.Failed, MapOutcome.Failed, and ParallelOutcome.Failed.
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
// The handler inspects MapOutcome.Failed and returns a partial result; the parent lands Done.
Console.WriteLine("--- scenario 1: map with one failing item, soft handling ---");
var mapOutcome = await jobs.ExecuteAndWaitAsync<RunMapSoft, MapReport>(new RunMapSoft());
Console.WriteLine($"parent: {mapOutcome.TerminalStatus}, report: {mapOutcome.Value!.Summary}");

// --- Scenario 2: ParallelAsync, ThrowIfAnyFailed escalation ---
// The handler calls ThrowIfAnyFailed after a branch fails; ChildGroupException makes the parent
// land Failed. The driver sees IsFailed without the exception propagating here.
Console.WriteLine("--- scenario 2: parallel with one failing branch, ThrowIfAnyFailed ---");
var parallelOutcome = await jobs.ExecuteAndWaitAsync<RunParallelEscalated>(new RunParallelEscalated());
Console.WriteLine($"parent: {parallelOutcome.TerminalStatus} (expected Failed)");

// --- Scenario 3: JoinAsync, ThrowIfAnyFailed escalation ---
// Same pattern for JoinOutcome: the handler escalates, parent lands Failed.
Console.WriteLine("--- scenario 3: join with one failing child, ThrowIfAnyFailed ---");
var joinOutcome = await jobs.ExecuteAndWaitAsync<RunJoinEscalated>(new RunJoinEscalated());
Console.WriteLine($"parent: {joinOutcome.TerminalStatus} (expected Failed)");

await host.StopAsync();

namespace Acta.Concepts.ChildFailureOutcomes
{
    // ---- scenario 1 inputs/outputs ----

    public readonly record struct RunMapSoft;

    public sealed record ProcessItem(string Id, bool ShouldFail);

    public sealed record MapReport(string Summary);

    // ---- scenario 2 inputs ----

    public readonly record struct RunParallelEscalated;

    public sealed record RunBranch(string Name, bool ShouldFail);

    // ---- scenario 3 inputs ----

    public readonly record struct RunJoinEscalated;

    public sealed record RunChild(string Name, bool ShouldFail);

    public sealed class FailureScenarioJobs
    {
        // Scenario 1: map over three items; one item is rigged to fail. After waiting, the handler
        // checks MapOutcome.Failed and returns a partial summary; the parent lands Done.
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

        // Scenario 2: parallel group with two branches; one branch fails. After waiting, the handler
        // calls ThrowIfAnyFailed; ChildGroupException propagates and the parent lands Failed.
        // MaxAttempts = 1 keeps the parent from retrying after the escalation throws.
        [Job("run-parallel-escalated", MaxAttempts = 1)]
        public async Task HandleParallelEscalated(RunParallelEscalated _, JobContext context, CancellationToken ct)
        {
            var result = await context.ParallelAsync(
                "analyze",
                p => p.Child("good", new RunBranch("good", ShouldFail: false)).Child("bad", new RunBranch("bad", ShouldFail: true)),
                ct
            );

            foreach (var kv in result.Failed)
            {
                Console.WriteLine($"  branch {kv.Key} failed (status={kv.Value.Status})");
            }

            // ThrowIfAnyFailed throws ChildGroupException; the parent lands Failed.
            result.ThrowIfAnyFailed();
        }

        // MaxAttempts = 1 so the failing branch terminates immediately.
        [Job("run-branch", MaxAttempts = 1)]
        public async Task RunBranchHandler(RunBranch input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            if (input.ShouldFail)
            {
                Console.WriteLine($"  branch {input.Name}: failing");
                throw new InvalidOperationException($"branch {input.Name} failed intentionally");
            }
            Console.WriteLine($"  branch {input.Name}: done");
        }

        // Scenario 3: two children started by hand and joined; one child fails. Handler calls
        // ThrowIfAnyFailed on the JoinOutcome; the parent lands Failed.
        // MaxAttempts = 1 keeps the parent from retrying after the escalation throws.
        [Job("run-join-escalated", MaxAttempts = 1)]
        public async Task HandleJoinEscalated(RunJoinEscalated _, JobContext context, CancellationToken ct)
        {
            var alpha = await context.StartChildAsync("alpha", new RunChild("alpha", ShouldFail: false), ct: ct);
            var beta = await context.StartChildAsync("beta", new RunChild("beta", ShouldFail: true), ct: ct);

            var result = await context.JoinAsync([alpha, beta], ct);

            foreach (var child in result.Failed)
            {
                Console.WriteLine($"  child {child.ChildJobId} failed (status={child.Status})");
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
