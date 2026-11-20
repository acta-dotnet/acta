# How to start a job from an HTTP endpoint and poll it

## Remember

- The name in `[Job("import-csv")]` and the namespace in `Run<AspNetEnqueueApiJobs>("aspnet-enqueue-api")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ImportCsv` is stored with each job as its input, and `ImportResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `import-csv-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
