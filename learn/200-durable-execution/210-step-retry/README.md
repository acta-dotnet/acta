# How to compensate when the last attempt fails

## Remember

- `"reserve-slot"`, `"call-weather-api"` and `"release-slot"` are the keys to each step's stored result. Rename `"reserve-slot"` and a retry reserves a second slot.
- Between two attempts a job waits in the database under its name. The name in `[Job("fetch-forecast")]` and the namespace in `Run<StepRetryJobs>("step-retry")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FetchForecast` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `fetch-forecast-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
