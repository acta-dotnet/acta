# How to keep a value across retries

## Remember

- `"synced-at-utc"` is the key to the stored value. A renamed variable reads as absent: a retry would not find what the earlier attempt saved.
- The name in `[Job("sync-users")]` and the namespace in `Run<DurableVariableJobs>("durable-variable")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SyncUsers` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `sync-users-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
