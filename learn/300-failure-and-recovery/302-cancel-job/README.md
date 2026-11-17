# How to cancel a job from inside its handler

## Remember

- `CancelAsync` ends the job by throwing, so nothing after it runs. `Cancelled` is not a failure: no retry and no alert follow.
