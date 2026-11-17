# How to limit how many times a job retries

## Remember

- `FailureCount` counts the attempts that failed since the job last succeeded, a crash included. It reads 0 again when the job succeeds; the events keep every failure.
- A crash spends an attempt: the attempt a dead worker held is reclaimed as a failed one.
- With no `MaxAttempts`, a job gets 15 attempts, backing off from a minute to a day.
