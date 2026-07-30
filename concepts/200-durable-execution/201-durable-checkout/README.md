<!-- engineering-lab
lab: what-if-the-job-engine-was-the-schema
also-labs: can-the-hot-job-row-stay-tiny
views: jobs_view, steps_view, checkpoints_view, events_view
alternatives: bare-retry, application-state-machine, external-idempotency, distributed-transaction, workflow-engine
-->

# Engineering Lab: the schema is the runtime

## The problem

A checkout crosses inventory, payment, human review, a time boundary, and notification. A process can
stop between any two instructions. Retrying a handler does not by itself say which work may repeat,
what the checkout is waiting for, or how an operator can prove what happened.

## Common approaches

- Keep the continuation in memory and lose it when the process stops.
- Put flags in an application table and build a state-machine runner around them.
- Replay everything and require every external system to honor an deduplication key.
- Adopt a workflow engine with a separate state model and operating surface.
- Use a distributed transaction where every participant genuinely supports one.

## Why this design

Acta gives one checkout identity several explicit relational facts: completed step outcomes, a durable
variable, a signal latch, a timer, a terminal result, and an append-only event ledger. The worker uses
those same facts to execute the job, while operators use curated views to understand it.

The job and runtime rows are deliberately split. Stable identity and input stay on `jobs`; frequently
updated status, scheduling, counters, and worker ownership live on the smaller `runtimes` row.

## Trade-offs

The handler re-enters from the top after each durable wait. Completed steps replay their stored outcome,
but a normal step body can still run again if the process dies after its external side effect and before
Acta records completion. The example therefore passes stable deduplication keys to its simulated external
systems. A real inventory, payment, or mail API must persist and honor those keys.

Every named step and checkpoint is also a durable contract and an additional row. This is more explicit
and inspectable than a process-local continuation, but it is not free and it is not exactly-once execution.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/201-durable-checkout
```

The program creates one uniquely scoped checkout, holds it long enough to inspect the enqueue state,
then walks automatically through four snapshots:

1. Enqueued but not yet executed.
2. Suspended on fraud review with no worker lease.
3. Waiting on a durable settlement timer with no worker lease.
4. Done, with steps, checkpoints, result, split job/runtime state, and events visible.

Use `--brief` to skip row output. Use `--all-columns` to add the complete `jobs_view` record at the
fraud-review boundary.

## Rows to inspect

The default Notice queries select only the fields that prove each claim. With `--all-columns`, the lab
first runs this explicit Explore query:

```sql
SELECT *
FROM jobs_view
WHERE job_id = @jobId;
```

The program primarily queries `jobs_view`, `steps_view`, `checkpoints_view`, and `events_view`. It goes
one level deeper only to compare `jobs` with `runtimes` and to show the terminal `results` row. Those
base tables explain Acta's design; applications should normally use `IJobs`, and operational tooling
should prefer the curated views. Base tables are not the storage compatibility contract.

Notice that `execution_number` reaches three while every successful step remains on attempt one. The
plain approval log appears twice because signal and timer recovery both re-enter the handler; the
completed step bodies are replay-skipped after their outcomes have been recorded.

## Break it

Reject the fraud review:

```bash
dotnet run --project concepts/200-durable-execution/201-durable-checkout -- --reject
```

The same signal mechanism now ends the checkout as `cancelled`. The resulting rows prove that no timer,
receipt step, or terminal result was created, while the completed inventory and payment evidence remains.

This lab does not stage a process kill. Use [`220-at-most-once-step`](../220-at-most-once-step/) for the
ambiguous side-effect crash window and [`705-worker-crash-recovery`](../../700-topology-and-deployment/705-worker-crash-recovery/)
for lease expiry and takeover by another worker.

## When not to use

Use a database transaction when all work is local and atomic. A short idempotent background action does
not need this many durable facts. Prefer an external workflow engine when non-developers must author large,
versioned processes or cross-system orchestration is itself the product.

## Source trail

- [The schema-as-runtime Engineering Lab](../../../docs/engineering-labs.md)
- [The split-state Engineering Lab](../../../docs/engineering-labs.md)
- [`Checkout.cs`](./Checkout.cs)
- [`JobContext.cs`](../../../src/Acta/Execution/JobContext.cs)
- [`Job.cs`](../../../src/Acta.Relational/Entities/Job.cs)
- [`JobRuntime.cs`](../../../src/Acta.Relational/Entities/JobRuntime.cs)
- [`JobCheckpoint.cs`](../../../src/Acta.Relational/Entities/JobCheckpoint.cs)
- [`JobStep.cs`](../../../src/Acta.Relational/Entities/JobStep.cs)
