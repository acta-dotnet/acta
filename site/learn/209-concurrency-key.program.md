# The whole program: limit how many jobs run at once for one key

The how-to is [/learn/209-concurrency-key.md](/learn/209-concurrency-key.md). This file holds every source file of `concepts/200-durable-execution/209-concurrency-key` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### concurrency-key.cs

```csharp
// How to limit how many jobs run at once for one key
// A `ConcurrencyKey` on the enqueue lets one job at a time run for that key. `ConcurrencyLimit` on the job gives a
// key several slots, and `RateLimit` meters how often jobs start.
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

// learn: 1. Give jobs that must not overlap the same key
// Shared concurrency key serializes whole jobs: the key is taken after claim, before the handler; a worker claiming the second job while it's held releases back to Ready (no executor tied up). Contrast 207-run-with-lock, which serializes only a section while the waiter holds its executor.
var first = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));
var second = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ConcurrencyKey("rebuild:acme"));

// learn: end
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
    SELECT job_id, job_ref, job_status, concurrency_key, leased_by_worker_id, next_run_at_utc
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

// The same gate with a size: [Job(ConcurrencyLimit = 2)] gives the key two concurrency slots, so two of these
// four run together and the rest bounce. No enqueue key here, so the definition name is the key.
var shards = new List<EnqueueJobResponse>();
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
    SELECT entity_id AS job_id, event, details_text, execution_number
    FROM events_view
    WHERE entity = 'job' AND entity_id IN (@firstJobId, @secondJobId)
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
    SELECT job_id, definition_name, job_status, concurrency_key
    FROM jobs_view
    WHERE definition_name = 'reindex-shard'
    ORDER BY job_id
    """
);

// The third gate: how OFTEN, not how many. [Job(RateLimit = "5/s")] meters starts against the
// database clock, and a job that arrives early is booked the next free instant rather than retried.
var pings = new List<EnqueueJobResponse>();
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
    "An early job a quarter second or more from its turn re-arms once, budget-neutral, at its reserved instant",
    """
    SELECT entity_id AS job_id, event, details_text, execution_number
    FROM events_view
    WHERE definition_name = 'ping-endpoint' AND event = 'job.rescheduled' AND details_text LIKE '%"reason":"rate-limited"%'
    ORDER BY event_id
    """
);
await host.StopAsync();

static async Task<int> ExecutingShardCountAsync(IJobs jobs, List<EnqueueJobResponse> shards, CancellationToken ct)
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
        // learn: 3. Limit how often jobs start
        // RateLimit is the other admission gate: five starts a second across the namespace's workers, metered
        // on the definition name because no RateName was given. Like ConcurrencyLimit it is an
        // operator-overridable policy value on the definitions row.
        [Job("ping-endpoint", RateLimit = "5/s")]
        // learn: end
        public Task Handle(PingEndpoint input, CancellationToken ct)
        {
            Console.WriteLine($"ping {input.Sequence} at {DateTime.UtcNow:HH:mm:ss.fff}");
            return Task.CompletedTask;
        }
    }

    public sealed class ReindexShardJob
    {
        // learn: 2. Let several run at once, up to a limit
        // ConcurrencyLimit is a per-definition policy value, so an operator can raise or lower it on the
        // definitions row without a deploy. Two at a time, whatever the fleet size.
        [Job("reindex-shard", ConcurrencyLimit = 2)]
        // learn: end
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
```

## Notes

### The problem

Two jobs for the same hot resource must not execute together. Taking a lock inside both handlers can
serialize the work, but the loser has already consumed an executor while it waits.

### Common approaches

| Mechanism | Best fit |
| --- | --- |
| `RunWithLockAsync` | A critical section inside a handler |
| `ConcurrencyKey` (+ `ConcurrencyLimit`) | Admission control for the whole execution, one job at a time or N |
| `RateLimit` | Admission control on how often, not how many |
| Lane (`.Lane(...)`, `[Job(Lane = ...)]`) | One job at a time per lane, in enqueue order |
| Queue partitioning | High-volume, stable partition keys |
| Database uniqueness | Preventing duplicate durable records |
| External distributed lock | Coordination beyond Acta's database |

### Why this design

Acta acquires the concurrency-key lease after claim but before handler invocation. A loser is re-armed
`ready` with a short delay and releases its worker. The event reason makes this budget-neutral bounce
visible instead of looking like an application failure.

`[Job(ConcurrencyLimit = N)]` sizes that gate. The key then owns N lock slot leases instead of one, so N
attempts run together and the N+1th bounces the same way. The lab's second handler, `reindex-shard`,
declares a limit of 2 and supplies no enqueue key, so the definition name is the key: a limit alone
throttles the whole definition. The limit is an operator-overridable policy value on the definition row.

`[Job(RateLimit = "N/s")]` is the third gate and meters how often attempts start, which a limit cannot
express: five a second is not the same shape as five at once. The lab's third handler,
`ping-endpoint`, declares "5/s". A job that arrives before its turn is booked the next free instant
and re-arms once at exactly that instant, rather than a crowd re-racing a counter; a turn less than a
quarter second away is waited out in process instead, with no re-arm. `RateName` puts several
definitions of the namespace on one meter; definitions that share one must declare the same rate.

`ConcurrencyKey` provides admission, not ordering. While workers hold valid leases on all of the key's
lock slots (one, unless a limit sizes it), no other job with that namespace and key is admitted; the exclusion is as strong as the lease, which a
heartbeat renews while the handler runs. Admission order is unspecified: under sustained arrivals that
keep a key held, an older job can be repeatedly overtaken, and Acta does not bound its wait. Use it for
exclusive *unordered* work.

### Trade-offs

Contention adds claim/re-arm traffic, and on a key that never goes idle a bounced job can starve. A
bounce returns the job to `ready` with its next run pushed a few seconds out, so at the instant the
key frees the bounced job is not in the claim candidate set at all: a job enqueued later can take the
key ahead of it, and the arrival after that can do the same. Ordering the claim scan differently
cannot repair that, because the starved job is not among the rows being ordered.

The lock is scoped to Acta work in this database; it does not coordinate unrelated applications
unless they share the same protocol.

### Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/209-concurrency-key
```

The lab discovers which racing job acquired the key, then inspects the owner and competitor before both
finish.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows the owner executing and the competitor ready without a worker. The lab also descends
one level into the internal `locks` table to reveal the named holder (the key's lock slot rows are
`<namespace id>.sem.<key>.<slot>`, and the meter's are its `.rate.ping-endpoint` bucket and
reservations), then uses `events_view` to show the `concurrency-key-held` and `rate-limited` reasons. Base tables are implementation detail; use the curated views or `IJobs` for
normal operations.

### Break it

Increase the handler delay and enqueue more jobs with the same key. Watch repeated admission bounces.
Then give each job a different key to see concurrency return.

### When not to use

Not when you need order. `ConcurrencyKey` is the middle rung of three, and only the third one orders
anything:

1. **Best-effort serial dispatch under restricted conditions.** One worker process for the namespace,
   `MaxConcurrentExecutors = 1`, equal priority, and jobs that are immediately due. That gives serial
   execution, not strict FIFO: retries, delayed eligibility, priority changes, and operator actions all
   reorder the queue, and Acta does not enforce that only one process runs the namespace, so this is an
   operational promise you keep, not an invariant Acta checks. The claim
   scan orders by priority, then by next-run instant, then by `JobId`; `JobId` is a stable
   tie-breaker within one claim, not a multi-producer FIFO guarantee, because database identities are
   allocation order, not commit order.
2. **Exclusive unordered work.** `ConcurrencyKey`, exactly as this lab shows it.
3. **Strict ordered processing.** A lane: `.Lane("customer-42")` on the enqueue, or
   `[Job(Lane = "...")]` on the definition, runs the lane's jobs one at a time in enqueue order.
   Head-of-line blocking is the price: a failing head retries in place and holds everything behind it,
   so the design needs a poison-item policy.

Use queue partitioning when per-key ordering must scale past one job at a time per lane. Use a unique constraint when the
real invariant is “only one record may exist.” Use an external lock when non-Acta participants must
coordinate too.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`concurrency-key.cs`](https://github.com/acta-dotnet/acta/blob/main/concepts/200-durable-execution/209-concurrency-key/concurrency-key.cs)
- [`ExecutionAdmission.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Engine/Worker/ExecutionAdmission.cs)
- [`LockEntity.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Entities/LockEntity.cs)
