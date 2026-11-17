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
the signal stores its payload and re-arms the same job; the handler re-enters and reads the latch, which stays set for any later replay.

## Trade-offs

The handler starts from the top on execution two. Only durable steps and checkpoints suppress repeated
work. Signals also require a reliable actor to raise the correct named latch and a policy for signals
that never arrive.

## Run the experiment

```bash
dotnet run --project learn/200-durable-execution/204-wait-signal
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
dotnet run --project learn/200-durable-execution/204-wait-signal -- start
dotnet run --project learn/200-durable-execution/204-wait-signal -- inspect <printed-job-ref>
dotnet run --project learn/200-durable-execution/204-wait-signal -- raise <printed-job-ref>
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
- [`RuntimeJobContext.cs`](../../../src/Acta/Engine/Worker/Context/RuntimeJobContext.cs)
- [`WaitJobSignal.sqlite.sql`](../../../src/Acta/Operations/Signals/WaitJobSignal/WaitJobSignal.sqlite.sql)

## Remember

- A `suspended` job is healthy. It is waiting, not stuck.
- The handler starts from the top when the signal arrives. Put work that must not repeat in a step.
- Decide what happens if nobody answers: `WaitSignalAsync<Decision>("approval", TimeSpan.FromDays(14), ct)` returns an outcome whose `TimedOut` is true when the deadline passed.
- A raise proves nothing about who sent it. Check the caller in your endpoint before you raise.
- A raise that comes before the handler reaches its wait is stored, and the wait reads it without parking.
- The typed value is null when the signal was raised without one. A value binds leniently, so give an enum a first value that grants nothing.
- A signal keeps the value of its first raise. Keep timestamps and per-attempt ids out of it, so a repeated raise answers `AlreadyInState`.

## Check yourself

**While the job waits for the signal, which worker holds it?**

- The worker that started it
- Whichever worker is idle
- No worker

<details><summary>Answer</summary>

No worker. The wait is a row. The job reads `suspended` with no worker and no lease, so the process can stop and the wait is still there.

</details>

**The signal is raised. On which execution does the job finish?**

- Execution 1 carries on where it stopped
- Execution 2, from the first line of the handler
- A new job is created

<details><summary>Answer</summary>

Execution 2, from the first line of the handler. Acta does not resume in the middle of a method. It runs the handler again, and the wait answers from its stored value.

</details>
