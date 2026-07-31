<!-- engineering-lab
lab: can-jobs-wait-without-workers
views: jobs_view, checkpoints_view
alternatives: blocked-worker, polling, self-reschedule, durable-signal, workflow-engine
-->

# Engineering Lab: waiting is a row, not a worker

## The problem

A job may wait hours or days for a person or another system. Keeping a task alive consumes capacity and
still loses the continuation when the process stops.

## Common approaches

- Block a task and hold process-local state.
- Poll until the external condition becomes true.
- Self-reschedule when the job can check readiness itself.
- Latch an explicit signal when another actor knows the event happened.
- Delegate a large externally orchestrated process to a workflow engine.

## Why this design

`WaitSignalAsync` writes a named checkpoint and ends the execution. The worker lease is cleared. Raising
the signal stores its payload and re-arms the same job; the handler re-enters and consumes the latch.

## Trade-offs

The handler starts from the top on execution two. Only durable steps and checkpoints suppress repeated
work. Signals also require a reliable actor to raise the correct named latch and a policy for signals
that never arrive.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/204-wait-signal
```

Press `S` when prompted. Use `--brief` for an automatic non-interactive run.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before the signal, `jobs_view` shows `suspended` with null worker/lease columns and
`checkpoints_view` shows a pending signal. After the raise, the checkpoint contains the latched value
and the same job finishes on execution two.

## Break it

Run a real two-process recovery against one configured database:

```bash
dotnet run --project concepts/200-durable-execution/204-wait-signal -- start
dotnet run --project concepts/200-durable-execution/204-wait-signal -- inspect <printed-job-ref>
dotnet run --project concepts/200-durable-execution/204-wait-signal -- raise <printed-job-ref>
```

`start` stops after suspension. `inspect` starts no worker and proves that the exact identity remains
suspended and unleased. `raise` does not enqueue another job; it signals that identity and waits for its
second execution to finish.

## When not to use

Use rescheduling instead when the job owns the readiness check. Use polling when the source cannot emit
a signal and the load is acceptable. Prefer a workflow product for very large, externally authored,
human-centric processes.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`wait-signal.cs`](./wait-signal.cs)
- [`RuntimeJobContext.cs`](../../../src/Acta.Runtime/Modules/Execution/RuntimeJobContext.cs)
- [`WaitSignal.sql`](../../../src/Acta.Sqlite/Sql/Execution/Signals/WaitSignal.sql)
