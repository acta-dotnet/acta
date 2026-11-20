# How to pause and resume a job from outside

## Remember

- An operator can pause, resume or restart a job long after it was enqueued, and the job asks for its handler by name. The name in `[Job("process-report")]` and the namespace in `Run<OperatorResumeJobs>("operator-resume")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
