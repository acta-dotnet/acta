# How to write a job that takes no input

## Remember

- The name in `[Job("heartbeat")]` and the namespace in `Run<NoInputJobJobs>("no-input-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
