# How to let one job at a time touch a resource

## Remember

- Acquiring the lock waits about 30 seconds, the `timeout:` argument, before it throws `LockAcquisitionTimeoutException`.
- The hold renews with the holder's heartbeat, so a dead holder keeps the lock for up to one lease.
- The name in `[Job("update-inventory")]` and the namespace in `Run<RunWithLockJobs>("run-with-lock")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `UpdateInventory` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `update-inventory-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
