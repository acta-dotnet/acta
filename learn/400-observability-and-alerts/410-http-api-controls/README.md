# How to control jobs over HTTP

## Remember

- The name in `[Job("sample-task")]` and the namespace in `Run<HttpApiControlsJobs>("http-api-controls")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SampleTask` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `sample-task-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
