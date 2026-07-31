<!-- engineering-lab
lab: can-restart-preserve-evidence-without-pretending-to-be-exactly-once
views: jobs_view, steps_view, events_view
alternatives: new-job, retry, operator-restart, manual-repair
-->

# Engineering Lab: restart re-arms identity; it does not promise exactly once

## The problem

An operator fixes an external dependency after a job exhausts its retry budget. They need to rerun the
same durable identity without erasing why it failed or accidentally assuming all handler code is safe.

## Common approaches

- Enqueue a brand-new job and correlate it manually.
- Increase automatic retries and wait.
- Repair the dependency, then restart the terminal identity.
- Perform the work manually outside the job system.

## Why this design

`RestartAsync` keeps `job_id`/`job_ref`, resets the failure budget and retention, increments later
execution numbers, and appends a restart event. Completed durable steps remain available to replay.

## Trade-offs

Restart is not exactly-once recovery. Bare handler code and incomplete steps may execute again. Retaining
step state is useful only if step names and meanings remain compatible with the restarted job.

## Run the experiment

```bash
dotnet run --project concepts/300-failure-and-recovery/310-operator-restart
```

The job fails three times, the lab marks its dependency repaired, and an operator restart succeeds.
Expected framework stack traces are suppressed so the four handler entries and durable state changes
remain readable; `events_view` retains the complete failure evidence.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before/after `jobs_view` proves stable identity, a higher execution number, and reset failure count.
`steps_view` keeps the successful preparation at attempt one; `events_view` appends rather than
rewrites history.

## Break it

Move the simulated side effect out of `RunStepAsync` and restart again. Its console output repeats. Then
interrupt a step before completion and compare with the at-most-once lab.

## When not to use

Enqueue a new identity when the business operation itself is new or needs separate retention/audit.
Avoid restart when handler evolution makes old payloads or durable names incompatible; migrate or repair
explicitly instead.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`operator-restart.cs`](./operator-restart.cs)
- [`RestartJob.sql`](../../../src/Acta.Sqlite/Sql/Execution/Jobs/RestartJob.sql)
