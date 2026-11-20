# How to get an alert when a job fails

## Remember

- The alert is not instant: `sys.alerts` turns failure events into alerts once a minute, so one appears up to about two minutes after the failure.
- The name in `[Job("process-report")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
