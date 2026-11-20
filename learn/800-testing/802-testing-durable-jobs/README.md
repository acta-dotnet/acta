# How to test a job that waits for a signal

## Remember

- `"reserve-stock"` and `"charge"` are the keys to each step's stored result, and `"approval"` names the signal the order waits on. A test that renames one is testing a different job from the one already stored.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("process-order")]` and the namespace in `Run<TestingDurableJobs>("testing-durable")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PlaceOrder` is stored with each job as its input, and `OrderResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
