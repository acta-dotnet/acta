<!-- engineering-lab
lab: can-jobs-wait-without-workers
views: jobs_view, checkpoints_view
alternatives: task-delay, periodic-timer, self-reschedule, durable-sleep, external-scheduler
-->

# Engineering Lab: durable sleep preserves an intention

## The problem

A process-local delay preserves a continuation only while that process lives. Long delays also tie the
design to one in-memory execution even though no useful work is happening.

## Common approaches

- `Task.Delay` or `PeriodicTimer` inside a live process.
- Self-reschedule and re-check readiness in the handler.
- Persist a named durable timer checkpoint.
- Let an external scheduler enqueue future work.

## Why this design

`SleepAsync` stores the due instant, re-arms the job for the future, clears its worker lease, and ends the
execution. When due, the handler re-enters and the elapsed named timer lets it continue.

## Trade-offs

Re-entry starts at the top; bare code before the sleep runs again. Wake-up precision is bounded by the
database clock and worker polling/wakeup behavior, and every timer adds durable rows and scheduling work.
Code after the sleep can also repeat if the process dies after its external side effect but before job
completion. Durable sleep preserves a due intention; it does not make later delivery exactly once.

## Run the experiment

```bash
dotnet run --project concepts/200-durable-execution/205-durable-sleep
```

The lab uses a three-second timer so both phases finish quickly.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows a future `next_run_at_utc` and no lease while `checkpoints_view` carries the timer.
The same job later reaches done with a higher execution number. Views are for operations and learning;
application code should normally use `IJobs`.

## Break it

Run a real process boundary against one configured database:

```bash
dotnet run --project concepts/200-durable-execution/205-durable-sleep -- start
dotnet run --project concepts/200-durable-execution/205-durable-sleep -- inspect <printed-job-ref>
dotnet run --project concepts/200-durable-execution/205-durable-sleep -- recover <printed-job-ref>
```

`start` persists a fifteen-second timer and exits. `inspect` starts no worker and proves the same job is
still stored without a lease. `recover` enqueues nothing: it starts a worker and waits for that exact
identity to re-enter and finish. Contrast this with `Task.Delay`, whose process-local continuation would
have disappeared.

## When not to use

Use `Task.Delay` for short process-local pacing that need not survive restart. Use an external scheduler
when another system owns the timing. Self-reschedule is clearer when each wake must re-check a changing
condition rather than merely wait for an instant.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`durable-sleep.cs`](./durable-sleep.cs)
- [`ArmOrConsumeSleepTimer.sql`](../../../src/Acta.Sqlite/Features/Execution/Sql/Timers/ArmOrConsumeSleepTimer.sql)
