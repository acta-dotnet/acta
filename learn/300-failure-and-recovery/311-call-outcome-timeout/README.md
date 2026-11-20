# How to read the outcome of a wait that can time out

## Remember

- The names in `[Job("slow-report")]`, `[Job("failing-report")]` and `[Job("success-report")]`, and the namespace in `Run<CallOutcomeTimeoutJobs>("call-outcome-timeout")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ReportResult` is stored with each job as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `slow-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
