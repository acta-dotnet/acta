# How to keep a repeated request from creating a second job

## Remember

- `handle.Action` says which happened: `EnqueueJobAction.Deduplicated` when the key matched a job already there, `Inserted` when this enqueue created it.
- The key prevents a second job row, not a second side effect. Execution stays at-least-once.
- Keys keep their case: `Order-1` and `order-1` are two keys, so lowercase an id yourself when its source ignores case.
- The name in `[Job("send-welcome-email")]` and the namespace in `Run<DeduplicationKeyJobs>("deduplication-key")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendWelcomeEmail` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-welcome-email-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
