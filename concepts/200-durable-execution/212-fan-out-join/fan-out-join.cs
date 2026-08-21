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
var outcome = await jobs.RunAndWaitAsync<AddNumbers, SumResult>(
    new AddNumbers([new(1, 250), new(251, 500), new(501, 750), new(751, 1000)])
);

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
        // One child per chunk, named by ordinal so replay dedupes; start all, then join and merge.
        [Job("add-numbers")]
        public async Task<SumResult> Handle(AddNumbers request, JobContext context, CancellationToken ct)
        {
            var children = new long[request.Chunks.Length];
            for (var i = 0; i < children.Length; i++)
            {
                var child = await context.StartChildAsync($"chunk-{i}", request.Chunks[i], ct);
                children[i] = child.JobId;
            }

            var outcomes = await context.WaitChildrenAsync(children, ct);
            if (outcomes.Any(o => !o.Succeeded))
            {
                await context.FailAsync("a chunk failed", ct);
            }

            var total = 0L;
            foreach (var childId in children)
            {
                total += (await context.GetChildResultAsync<PartialSum>(childId, ct))!.Value;
            }

            return new SumResult(total);
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
