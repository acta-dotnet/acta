# How to fail a job for good, with no retry

## Remember

- `FailAsync` ends the job by throwing, so nothing after it runs. The compiler cannot see that: in a handler that returns a value, write `return` or `throw` after it.
- A job ended this way reads `Failed` with `FailureCount` 0: no attempt failed, the handler ended it.
- A thrown exception is retried unless it is one Acta never retries, such as `NotSupportedException`, or the application declared its type final with `j.DoNotRetry<TException>()` in `UseActa`. `FailAsync` ends this one job; `DoNotRetry` applies to every job that throws the type.
- The name in `[Job("process-upload")]` and the namespace in `Run<FailJobJobs>("fail-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessUpload` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-upload-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
