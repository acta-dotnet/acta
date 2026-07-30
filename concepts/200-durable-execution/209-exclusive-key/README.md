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

## Trade-offs

Contention adds claim/re-arm traffic and can reduce fairness on very hot keys. The lock is scoped to
Acta work in this database; it does not coordinate unrelated applications unless they share the same
protocol.

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

Use queue partitioning for sustained, high-volume per-key ordering. Use a unique constraint when the
real invariant is “only one record may exist.” Use an external lock when non-Acta participants must
coordinate too.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`exclusive-key.cs`](./exclusive-key.cs)
- [`JobRunner.cs`](../../../src/Acta.Runtime/Modules/Execution/JobRunner.cs)
- [`Lease.cs`](../../../src/Acta.Relational/Entities/Lease.cs)
