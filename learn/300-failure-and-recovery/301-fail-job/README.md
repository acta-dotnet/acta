# How to fail a job for good, with no retry

## Remember

- `FailAsync` ends the job by throwing, so nothing after it runs. The compiler cannot see that: in a handler that returns a value, write `return` or `throw` after it.
- A job ended this way reads `Failed` with `FailureCount` 0: no attempt failed, the handler ended it.
