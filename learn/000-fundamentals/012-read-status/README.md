# How to read a job's status

## Remember

- `ExecutionNumber` counts every claim of the job, each resume after a wait and each bounce off a full concurrency key included. Show `FailureCount` as the tries.
- A sleeping job reads `ready` with a future `NextRunAtUtc`; a job waiting for a signal or a child reads `suspended`. Both are healthy.
- `executing` means claimed, not that a process is alive: `LeaseLapsed` is true for an executing job whose worker stopped heartbeating.
- The text of a status is `job.Status.Code`, such as `suspended`. `ToString()` gives the C# name, `Suspended`.
- The name in `[Job("send-welcome-email")]` and the namespace in `Run<ReadStatusJobs>("read-status")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
