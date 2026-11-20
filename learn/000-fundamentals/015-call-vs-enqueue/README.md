# How to choose between starting a job and waiting for it

## Remember

- The name in `[Job("add-numbers")]` and the namespace in `Run<CallVsEnqueueJobs>("call-vs-enqueue")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `AddNumbers` is stored with each job as its input, and `AddNumbersResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `add-numbers-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
