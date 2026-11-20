# How to restart a crashed app on SQLite and get its work back

## Remember

- On SQLite a restart brings a lost attempt back at once: a starting worker releases the work of processes on the same machine that have ended. A server database waits out the lease instead, as in [recover the work of a killed worker](../705-worker-crash-recovery/).
- A crash counts against the retry budget, and the job it took back is due again at once, without the backoff.
- The handler runs again from its first line. Work that finished before the crash and must not repeat belongs in a step.
- The lost attempt shows as `job.execution-orphaned` on the job's timeline, and Explain counts it as a lost lease.
- A job that is waiting or sleeping holds no lease and needs no recovery.
- The name in `[Job("export")]` and the namespace in `Run<SqliteRestartJobs>("sqlite-restart")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `Export` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `export-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**The app is killed while one of its jobs waits for a signal. What does the restarted app have to recover for that job?**

- The job, once its lease has run out
- Nothing
- The signal, which has to be raised again

<details><summary>Answer</summary>

Nothing. A waiting job is a row with no worker and no lease, so the kill took nothing from it. It carries on when its signal is raised, whichever process is running then.

</details>
