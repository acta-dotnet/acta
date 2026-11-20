# How to read why a job did what it did

## Remember

- A timeline starts at the first attempt. The enqueue is the job's row, `CreatedAtUtc`, not an event.
- A wait writes two events at one instant: `job.execution-yielded` and `job.suspended`. A sleep writes `job.execution-yielded` and `job.slept`.
- `new ListEventsQuery(LineageRoot: root.JobRef)` reads a whole tree's timeline in one query, children included.
- `"collect"` is how `run-report` finds the child it already started. A renamed child is new work.
- A parent and its children are each stored under their own job name. The names in `[Job("run-report")]` and `[Job("collect-data")]`, and the namespace in `Run<ReadEventTimelineJobs>("read-event-timeline")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `RunReport` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `run-report-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
