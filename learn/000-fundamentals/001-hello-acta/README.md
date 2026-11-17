# How to run your first job

## Remember

- The name in `[Job("hello")]` goes into the database, the dashboard, and alerts. Keep it lowercase with dashes, and treat it as permanent once jobs exist under it.
- The namespace in `Run<HelloActaJobs>("hello-acta")` is permanent too: every job is stored under it and there is no rename. Name it for the work the app owns, such as `billing`, not for an environment or a version.
- `MigrateOnStartup = true` is for your own machine. In production a deploy step runs `jobs migrate`.
- The process that enqueues can also run the job. You can split the two later without changing the handler.

## Check yourself

**`EnqueueAsync` has returned and the worker has not picked the job up yet. Where is the job?**

- In the memory of the running process
- In a row of the SQLite file
- In a separate queue service

<details><summary>Answer</summary>

In a row of the SQLite file. The job is a durable row before anything runs it. Stop the process now and the row is still there for the next start.

</details>
