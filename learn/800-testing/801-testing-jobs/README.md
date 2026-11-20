# How to test a handler with the test host

## Remember

- The name in `[Job("echo")]` and the namespace in `Run<TestingJobs>("testing-jobs")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `Echo` is stored with each job as its input, and `EchoResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `echo-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
