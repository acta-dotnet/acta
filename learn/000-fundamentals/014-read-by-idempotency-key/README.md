# How to find a job by its idempotency key

## Remember

- The name in `[Job("send-welcome-email")]` and the namespace in `Run<ReadByIdempotencyKeyJobs>("read-by-idempotency-key")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
