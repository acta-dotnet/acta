# How to report how far along a job is

## Remember

- `SetProgressAsync` keeps one value, last write wins, as the checkpoint `sys.progress`. A value your own screen reads goes in a variable.
- The name in `[Job("import-file")]` and the namespace in `Run<ProgressJobs>("progress")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ImportFile` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `import-file-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
