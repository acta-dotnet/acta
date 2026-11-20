# How to pass a large file by reference

## Remember

- The name in `[Job("process-export")]` and the namespace in `Run<LargePayloadReferenceJobs>("large-payload-reference")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessExport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-export-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
