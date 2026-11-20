# How to handle child jobs that fail

## Remember

- A child alerts on its own failure even when the parent handles it. When the parent reports the failure, declare the child `AlertProfile = AlertProfileCode.None`.
- A failed child carries the message it failed with in `FailureMessage`.
- `ThrowIfAnyFailed()` fails the parent at once, with no retry: a retry would find the same failed child. To recover, restart the child, then the parent.
- `"process"`, `"alpha"` and `"beta"` are how each parent finds the children it already started. A renamed child is new work.
- A parent and its children are each stored under their own job name. The name in each `[Job(...)]`, such as `[Job("run-map-soft")]`,, and the namespace in `Run<ChildFailureOutcomesJobs>("child-failure-outcomes")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `MapReport` is stored with each job as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `run-map-soft-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
