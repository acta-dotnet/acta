# How to limit how many times a job retries

## Remember

- `FailureCount` counts the attempts that failed since the job last succeeded, a crash included. It reads 0 again when the job succeeds; the events keep every failure.
- A crash spends an attempt: the attempt a dead worker held is reclaimed as a failed one.
- With no `MaxAttempts`, a job gets 15 attempts, backing off from a minute to a day.
- Between two attempts a job waits in the database under its name. The name in `[Job("flaky-work")]` and the namespace in `Run<MaxAttemptsJobs>("max-attempts")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FlakyWork` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `flaky-work-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
