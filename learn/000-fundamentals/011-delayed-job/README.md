# How to run a job once, later

## Remember

- `ScheduleAsync(input, DateTimeOffset)` runs the job at an instant instead of after a delay. Work that recurs is a `[Schedule]`, not a job that schedules itself.
- A delayed job can sit in the database for days before it runs. The name in `[Job("send-reminder")]` and the namespace in `Run<DelayedJobJobs>("delayed-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendReminder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-reminder-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
