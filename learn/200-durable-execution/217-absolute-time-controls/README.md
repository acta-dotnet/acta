# How to schedule, sleep, and reschedule to an exact instant

## Remember

- `"report-delay"` names the timer `generate-report` sleeps on. A renamed timer is a different timer, so let the sleeping jobs finish before you rename it.
- A delayed job can sit in the database for days before it runs. The names in `[Job("generate-report")]` and `[Job("poll-external")]`, and the namespace in `Run<AbsoluteTimeControlsJobs>("absolute-time-controls")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `GenerateReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `generate-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
