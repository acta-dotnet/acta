# How to show where each job is on your own page

## Remember

- `executing` means claimed, not that a process is alive: `LeaseLapsed` is true for an executing job whose worker stopped heartbeating.
- A sleeping job reads `ready` with a future `NextRunAtUtc`; a job waiting for a signal or a child reads `suspended`. Both are healthy.
- `ExecutionNumber` counts every claim of the job, each resume after a wait and each bounce off a full concurrency key included. Show `FailureCount` as the tries.
- The text of a status is `job.Status.Code`, such as `suspended`. `ToString()` gives the C# name, `Suspended`.
- Write the screen's value inside a step. A bare write before a wait runs again when the handler is entered again, and the page would step back for a moment.
- These calls read one job. To find many, [list and page through jobs](../409-operator-queries/).

## Check yourself

**A job reads `executing`, its worker was killed, and its lease has run out. What should the page show?**

- Running
- Interrupted
- Failed

<details><summary>Answer</summary>

Interrupted. `executing` says a worker claimed the job, not that the worker is alive. `LeaseLapsed` is true once its lease has run out, and recovery brings the job back by itself; nothing has failed.

</details>
