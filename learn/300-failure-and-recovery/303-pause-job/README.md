# How to pause a job from inside its handler

## Remember

- An operator can pause, resume or restart a job long after it was enqueued, and the job asks for its handler by name. The name in `[Job("apply-migration")]` and the namespace in `Run<PauseJobJobs>("pause-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ApplyMigration` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `apply-migration-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
