# How to fail a job for good, with no retry

## Remember

- `FailAsync` ends the job by throwing, so nothing after it runs. The compiler cannot see that: in a handler that returns a value, write `return` or `throw` after it.
- A job ended this way reads `Failed` with `FailureCount` 0: no attempt failed, the handler ended it.
- A thrown exception is retried unless it is one Acta never retries, such as `NotSupportedException`, or the application declared its type final with `j.DoNotRetry<TException>()` in `UseActa`. `FailAsync` ends this one job; `DoNotRetry` applies to every job that throws the type.
