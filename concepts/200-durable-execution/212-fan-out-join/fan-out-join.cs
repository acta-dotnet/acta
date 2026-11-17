// How to fan out over a list and merge the results
// `MapAsync` starts one child job per item of a list, waits for them all, and hands back each item's result for the
// parent to merge.
using Acta;
using Acta.Concepts.FanOutJoin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FanOutJoinJobs>("fan-out-join");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Map-reduce as child jobs: replay finds the same children by name, so no chunk is summed twice.
// Compare 601-fan-out, where the children are fire-and-forget and nothing merges.
var outcome = await jobs.CallAsync(FanOutJoinJobs.AddNumbers(new AddNumbers([new(1, 250), new(251, 500), new(501, 750), new(751, 1000)])));

Console.WriteLine($"sum 1..1000 = {outcome.Value!.Total}");

await host.StopAsync();

namespace Acta.Concepts.FanOutJoin
{
    public sealed record AddNumbers(SumRange[] Chunks);

    public sealed record SumRange(long From, long To);

    public sealed record PartialSum(long Value);

    public sealed record SumResult(long Total);

    public sealed class AddNumbersJobs
    {
        [Job("add-numbers")]
        public async Task<SumResult> Handle(AddNumbers request, JobContext context, CancellationToken ct)
        {
            // learn: 1. Map the list onto child jobs
            // MapAsync starts one child per chunk and waits for all of them. Each child is keyed by its range,
            // so a replay finds the same children by name. A child given as its generated call types the map,
            // so each item's outcome carries the child's result and the merge reads no child by id.
            var chunks = await context.MapAsync(
                "chunk",
                request.Chunks,
                itemKey: chunk => $"{chunk.From}-{chunk.To}",
                child: chunk => FanOutJoinJobs.SumRange(chunk),
                ct
            );
            // learn: end

            // learn: 2. Stop if a child failed, then merge the results
            // The map does not throw for a failed child; the parent asks, and decides.
            if (!chunks.Succeeded)
            {
                await context.FailAsync("a chunk failed", ct);
            }

            return new SumResult(chunks.Items.Sum(item => item.Outcome.ValueOrThrow().Value));
            // learn: end
        }

        [Job("sum-range")]
        public async Task<PartialSum> Sum(SumRange range, CancellationToken ct)
        {
            await Task.Delay(150, ct);

            var sum = 0L;
            for (var n = range.From; n <= range.To; n++)
            {
                sum += n;
            }

            Console.WriteLine($"chunk {range.From}..{range.To} = {sum}");
            return new PartialSum(sum);
        }
    }
}
