<!-- engineering-lab
lab: can-hot-keys-stop-blocking-workers
views: jobs_view, events_view
alternatives: critical-section-lock, whole-job-admission, queue-partition, unique-constraint, external-lock
-->

# Engineering Lab: admission control without a blocked worker

## The problem

Two jobs for the same hot resource must not execute together. Taking a lock inside both handlers can
serialize the work, but the loser has already consumed an executor while it waits.

## Common approaches

| Mechanism | Best fit |
| --- | --- |
| `RunWithLockAsync` | A critical section inside a handler |
| `ExclusiveKey` | Admission control for the whole execution |
| Queue partitioning | High-volume, stable partition keys |
| Database uniqueness | Preventing duplicate durable records |
| External distributed lock | Coordination beyond Acta's database |

## Why this design

Acta acquires the exclusive-key lease after claim but before handler invocation. A loser is re-armed
`ready` with a short delay and releases its worker. The event reason makes this budget-neutral bounce
visible instead of looking like an application failure.

`ExclusiveKey` provides mutual exclusion, not ordering. At most one job per namespace and key
executes at a time. Admission order is unspecified: under sustained arrivals that keep a key held, an
older job can be repeatedly overtaken, and Acta does not bound its wait. Use it for exclusive
*unordered* work.

## Trade-offs

Contention adds claim/re-arm traffic, and on a key that never goes idle a bounced job can starve. A
bounce returns the job to `ready` with its next run pushed a few seconds out, so at the instant the
key frees the bounced job is not in the claim candidate set at all: a job enqueued later takes the
key ahead of it, and the arrival after that can do the same. Ordering the claim scan differently
cannot repair that, because the starved job is not among the rows being ordered.

The lock is scoped to Acta work in this database; it does not coordinate unrelated applications
unless they share the same protocol.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/209-exclusive-key
```

The lab discovers which racing job acquired the key, then inspects the owner and competitor before both
finish.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows the owner executing and the competitor ready without a worker. The lab also descends
one level into the internal `leases` table to reveal the named holder, then uses `events_view` to show
`job.exclusive-key-held`. Base tables are implementation detail; use the curated views or `IJobs` for
normal operations.

## Break it

Increase the handler delay and enqueue more jobs with the same key. Watch repeated admission bounces.
Then give each job a different key to see concurrency return.

## When not to use

Not when you need order. `ExclusiveKey` is the middle rung of three, and only the third one orders
anything:

1. **Best-effort serial dispatch under restricted conditions.** One worker process for the namespace,
   `MaxConcurrentExecutors = 1`, `ClaimBatchSize = 1`, equal priority, and jobs that are immediately
   due. That gives serial execution, not strict FIFO: retries, delayed eligibility, priority changes,
   and operator actions all reorder the queue, and Acta does not enforce that only one process runs
   the namespace, so this is an operational promise you keep, not an invariant Acta checks. The claim
   scan orders by priority, then by next-run instant, then by `JobId`; `JobId` is a stable
   tie-breaker within one claim, not a multi-producer FIFO guarantee, because database identities are
   allocation order, not commit order.
2. **Exclusive unordered work.** `ExclusiveKey`, exactly as this lab shows it.
3. **Strict ordered processing.** A durable coordinator or chain that releases item N+1 only once
   item N has reached the required outcome. Head-of-line blocking is the price: one stuck item holds
   everything behind it, so the design needs a poison-item policy.

Use queue partitioning for sustained, high-volume per-key ordering. Use a unique constraint when the
real invariant is “only one record may exist.” Use an external lock when non-Acta participants must
coordinate too.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`exclusive-key.cs`](./exclusive-key.cs)
- [`JobExecution.cs`](../../../src/Acta.Runtime/Modules/Execution/JobExecution.cs)
- [`Lock.cs`](../../../src/Acta.Relational/Entities/Lock.cs)
