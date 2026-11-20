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
execution numbers, and appends a restart event. Completed durable steps remain available to replay;
a failed at-most-once step runs again, while an interrupted one stays until the handler
reconciles it. A laned job can come back `Blocked` behind its lane's running job, and an `Executing` job
is refused.

## Trade-offs

Restart is not exactly-once recovery. Bare handler code and incomplete steps may execute again. Retaining
step state is useful only if step names and meanings remain compatible with the restarted job.

## Run the experiment

```bash
dotnet run --project learn/300-failure-and-recovery/310-operator-restart
```

The job fails three times, the lab marks its dependency repaired, and an operator restart succeeds.
Expected framework stack traces are suppressed so the four handler entries and durable state changes
remain readable; `events_view` retains the complete failure evidence.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

Before/after `jobs_view` proves stable identity, a higher execution number, and reset failure count.
`steps_view` keeps the successful preparation succeeded, so it does not run again; `events_view`
appends rather than rewrites history.

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
- [`RestartJob.sqlite.sql`](../../../src/Acta/Operations/Jobs/RestartJob/RestartJob.sqlite.sql)

## Remember

- A restart keeps every succeeded step: those steps return what they computed before.
- A parent that failed on a child fails again when it is restarted alone. Restart the child, then the parent once the child has succeeded.
- `"prepare-report"` is the key to the step's stored result, which is why a restart does not prepare the report again. Rename the step and it does.
- An operator can pause, resume or restart a job long after it was enqueued, and the job asks for its handler by name. The name in `[Job("flaky-report")]` and the namespace in `Run<OperatorRestartJobs>("operator-restart")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FlakyReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `flaky-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
