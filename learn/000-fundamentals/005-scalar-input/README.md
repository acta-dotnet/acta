# How to pass one plain value as the input

## Remember

- The name in `[Job("shout")]` and the namespace in `Run<ScalarInputJobs>("scalar-input")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
