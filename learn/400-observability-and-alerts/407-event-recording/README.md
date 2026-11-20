# How to choose how many events a job records

## Remember

- The names in `[Job("audit-work")]` and `[Job("failures-work")]`, and the namespace in `Run<EventRecordingJobs>("event-recording")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `AuditWork` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `audit-work-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
