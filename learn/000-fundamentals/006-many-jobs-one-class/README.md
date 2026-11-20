# How to keep several jobs in one class

## Remember

- The names in `[Job("shout")]`, `[Job("double-it")]` and `[Job("halve")]`, and the namespace in `Run<ManyJobsOneClassJobs>("many-jobs-one-class")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
