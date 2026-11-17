# How to read why a job did what it did

## Remember

- A timeline starts at the first attempt. The enqueue is the job's row, `CreatedAtUtc`, not an event.
- A wait writes two events at one instant: `job.execution-yielded` and `job.suspended`.
- `new ListEventsQuery(LineageRoot: root.JobRef)` reads a whole tree's timeline in one query, children included.
