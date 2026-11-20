# How to enqueue many jobs in one round trip

## Remember

- The name in `[Job("ping")]` and the namespace in `Run<BatchEnqueueJobs>("batch-enqueue")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
