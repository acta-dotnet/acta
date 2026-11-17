# How to read a job's result

## Remember

- `GetResultAsync<T>` throws when there is no result; `TryGetResultAsync<T>` answers `(Found, Value)`. Neither read waits for the job.
