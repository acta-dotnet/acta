# How to run a job on a repeating schedule

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine.
- A permanent job keeps its step results from one firing to the next, so a step that succeeded answers the next firing from its record. End the handler with `await ctx.ResetStateAsync(ct)`, as its last action, and each firing starts clean. A step named per firing, `$"send.f{ctx.FiringId:N}"`, also runs every time, but each firing then leaves a step row that is kept as long as the job, and a permanent job lives as long as its schedule.
- The handler does its bounded work and returns. Never loop inside it: the schedule brings the next firing.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("purge-stale-sessions")]` and the namespace in `Run<RecurringJobs>("recurring-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
