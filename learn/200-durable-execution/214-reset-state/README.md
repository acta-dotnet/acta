# How to start each firing of a recurring job clean

## Remember

- `"reading-count"` is the key to the stored count. A renamed variable reads as absent.
- The name in `[Job("collect-metrics")]` and the namespace in `Run<ResetStateJobs>("reset-state")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
