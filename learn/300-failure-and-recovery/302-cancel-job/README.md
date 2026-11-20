# How to cancel a job from inside its handler

## Remember

- `CancelAsync` ends the job by throwing, so nothing after it runs. `Cancelled` is not a failure: no retry and no alert follow.
- The name in `[Job("import-feed")]` and the namespace in `Run<CancelJobJobs>("cancel-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ImportFeed` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `import-feed-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
