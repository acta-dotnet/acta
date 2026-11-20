# How to run a job on a repeating schedule

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine.
- A permanent job keeps its step results from one firing to the next. Put the firing in a step's name, `$"send.f{ctx.FiringId:N}"`, or the step that ran on the last firing answers this one from its record.
- The handler does its bounded work and returns. Never loop inside it: the schedule brings the next firing.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("purge-stale-sessions")]` and the namespace in `Run<RecurringJobs>("recurring-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
