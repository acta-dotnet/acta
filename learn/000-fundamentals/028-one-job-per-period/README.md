# How to allow one job per hour or per day

## Remember

- A delayed job can sit in the database for days before it runs. The name in `[Job("nightly-summary")]` and the namespace in `Run<OneJobPerPeriodJobs>("one-job-per-period")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `NightlySummary` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `nightly-summary-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
