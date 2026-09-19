using Acta;
using Acta.Concepts.ConcurrencyKey;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ConcurrencyKeyJobs>("concurrency-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Shared concurrency key serializes whole jobs: the key is taken after claim, before the handler; a worker claiming the second job while it's held releases back to Ready (no executor tied up). Contrast 207-run-with-lock, which serializes only a section while the waiter holds its executor.
var first = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
var second = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
Console.WriteLine("Enqueued two jobs with the same concurrency key.");

long ownerJobId;
long competitorJobId;
using var admissionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var firstStatus = (await jobs.GetAsync(first, admissionTimeout.Token))?.Status;
    var secondStatus = (await jobs.GetAsync(second, admissionTimeout.Token))?.Status;
    if (firstStatus == JobStatusCode.Executing && secondStatus == JobStatusCode.Ready)
    {
        ownerJobId = first.JobId;
        competitorJobId = second.JobId;
        break;
    }
    if (secondStatus == JobStatusCode.Executing && firstStatus == JobStatusCode.Ready)
    {
        ownerJobId = second.JobId;
        competitorJobId = first.JobId;
        break;
    }
    if (firstStatus?.IsTerminal == true && secondStatus?.IsTerminal == true)
    {
        throw new InvalidOperationException(
            "Both jobs finished before the admission boundary was observed; increase the lab handler delay."
        );
    }
    await Task.Delay(50, admissionTimeout.Token);
}
await lab.ShowAllAsync(
    "Explore both complete job records at the admission boundary",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id IN (@ownerJobId, @competitorJobId)
    ORDER BY CASE WHEN job_id = @ownerJobId THEN 0 ELSE 1 END
    """,
    new { ownerJobId, competitorJobId }
);
await lab.ShowAsync(
    "One job executes; its competitor returns to Ready without holding a worker",
    """
    SELECT job_id, job_ref, status, concurrency_key, leased_by_worker_id, next_run_at_utc
    FROM jobs_view
    WHERE job_id IN (@ownerJobId, @competitorJobId)
    ORDER BY CASE WHEN job_id = @ownerJobId THEN 0 ELSE 1 END
    """,
    new { ownerJobId, competitorJobId }
);
await lab.ShowAsync(
    "The concurrency key is a named lock owned by the running job",
    """
    SELECT lock_key, job_id, expires_at_utc, hold_token
    FROM {{schema}}.locks
    WHERE job_id = @ownerJobId
    """,
    new { ownerJobId }
);

// The same gate with a size: [Job(ConcurrencyLimit = 2)] gives the key two slots, so two of these
// four run together and the rest bounce. No enqueue key here, so the definition name is the key.
var shards = new List<JobEnqueueOutcome>();
for (var i = 0; i < 4; i++)
{
    shards.Add(await jobs.EnqueueAsync(new ReindexShard(i)));
}
Console.WriteLine("Enqueued four jobs against a definition limited to two at a time.");

// Wait for the slots to be taken; the rows below only exist while handlers hold them.
using var slotTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (await ExecutingShardCountAsync(jobs, shards, slotTimeout.Token) < 2)
{
    await Task.Delay(50, slotTimeout.Token);
}
await lab.ShowAsync(
    "A sized key is two lock rows, one per slot",
    """
    SELECT lock_key, job_id, expires_at_utc
    FROM {{schema}}.locks
    WHERE lock_key LIKE '%.sem.reindex-shard.%'
    ORDER BY lock_key
    """
);

using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (
    (await jobs.GetAsync(first, completionTimeout.Token))?.Status.IsTerminal != true
    || (await jobs.GetAsync(second, completionTimeout.Token))?.Status.IsTerminal != true
)
{
    await Task.Delay(100, completionTimeout.Token);
}
await lab.ShowAsync(
    "The event ledger explains the budget-neutral admission bounce",
    """
    SELECT job_id, event, to_status, reason, execution_number
    FROM events_view
    WHERE job_id IN (@firstJobId, @secondJobId)
    ORDER BY event_id
    """,
    new { firstJobId = first.JobId, secondJobId = second.JobId }
);

using var shardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
foreach (var shard in shards)
{
    while ((await jobs.GetAsync(shard, shardTimeout.Token))?.Status.IsTerminal != true)
    {
        await Task.Delay(100, shardTimeout.Token);
    }
}
await lab.ShowAsync(
    "All four shards finish, two at a time",
    """
    SELECT job_id, job_name, status, concurrency_key
    FROM jobs_view
    WHERE job_name = 'reindex-shard'
    ORDER BY job_id
    """
);

// The third gate: how OFTEN, not how many. [Job(RateLimit = "5/s")] meters starts against the
// database clock, and a job that arrives early is booked the next free instant rather than retried.
var pings = new List<JobEnqueueOutcome>();
for (var i = 0; i < 12; i++)
{
    pings.Add(await jobs.EnqueueAsync(new PingEndpoint(i)));
}
Console.WriteLine("Enqueued twelve jobs against a definition limited to five a second.");

using var pingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
foreach (var ping in pings)
{
    while ((await jobs.GetAsync(ping, pingTimeout.Token))?.Status.IsTerminal != true)
    {
        await Task.Delay(100, pingTimeout.Token);
    }
}

// The meter is two row shapes in `locks`, never a counter table: one bucket row holding the next
// arrival time, plus one reservation row per job still waiting for its turn. Both are usually gone by
// the time the backlog has drained, which is itself the point.
await lab.ShowAsync(
    "The rate meter is a bucket row plus one reservation per waiting job",
    """
    SELECT lock_key, job_id, expires_at_utc
    FROM {{schema}}.locks
    WHERE lock_key LIKE '%.rate.ping-endpoint%'
    ORDER BY lock_key
    """
);
await lab.ShowAsync(
    "Every early job re-armed exactly once, budget-neutral, at its reserved instant",
    """
    SELECT job_id, event, to_status, reason, execution_number
    FROM events_view
    WHERE job_name = 'ping-endpoint' AND reason = 'job.rate-limited'
    ORDER BY event_id
    """
);
await host.StopAsync();

static async Task<int> ExecutingShardCountAsync(IJobs jobs, List<JobEnqueueOutcome> shards, CancellationToken ct)
{
    var executing = 0;
    foreach (var shard in shards)
    {
        if ((await jobs.GetAsync(shard, ct))?.Status == JobStatusCode.Executing)
        {
            executing++;
        }
    }
    return executing;
}

namespace Acta.Concepts.ConcurrencyKey
{
    public sealed record RebuildIndex(string Tenant);

    public sealed record ReindexShard(int Shard);

    public sealed record PingEndpoint(int Sequence);

    public sealed class PingEndpointJob
    {
        // RateLimit is the other admission gate: five starts a second across the whole fleet, metered
        // on the definition name because no RateKey was given. Like ConcurrencyLimit it is an
        // operator-overridable policy slot on the definitions row.
        [Job("ping-endpoint", RateLimit = "5/s")]
        public Task Handle(PingEndpoint input, CancellationToken ct)
        {
            Console.WriteLine($"ping {input.Sequence} at {DateTime.UtcNow:HH:mm:ss.fff}");
            return Task.CompletedTask;
        }
    }

    public sealed class ReindexShardJob
    {
        // ConcurrencyLimit is a per-definition policy slot, so an operator can raise or lower it on the
        // definitions row without a deploy. Two at a time, whatever the fleet size.
        [Job("reindex-shard", ConcurrencyLimit = 2)]
        public async Task Handle(ReindexShard input, CancellationToken ct)
        {
            Console.WriteLine($"shard {input.Shard} started");
            await Task.Delay(1000, ct);
            Console.WriteLine($"shard {input.Shard} finished");
        }
    }

    public sealed class RebuildIndexJob
    {
        private static int _n;

        [Job("rebuild-index")]
        public async Task Handle(RebuildIndex input, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _n);
            Console.WriteLine($"[{input.Tenant}] rebuild #{n} started");
            await Task.Delay(3000, ct);
            Console.WriteLine($"[{input.Tenant}] rebuild #{n} finished");
        }
    }
}
