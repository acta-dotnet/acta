# How to check, delete, and default a job's variables

## Remember

- A variable write is durable by itself and needs no step around it. Read it from outside the job with `jobs.GetVariableOrDefaultAsync<T>(jobRef, name, ct)`.
- `"counter"` and `"raw-var"` are keys to stored values. A renamed variable reads as absent. To rename one, read the new name, then the old, and write the new.
- The name in `[Job("inspect-variables")]` and the namespace in `Run<VariableLifecycleJobs>("variable-lifecycle")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
