# How to fan out to child jobs with Map or WaitChildren

## Remember

- A child's name on the job reads is its `DeduplicationKey`: the call's name for `EnqueueChildAsync`, `{map}/{key}` for a map's child.
- A group waits for every child. It never fails fast and never cancels siblings.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.
- `"exif"`, `"blurhash"` and the other child names are how `process-photo` finds the children it already started. A renamed child is new work.
- A parent and its children are each stored under their own job name. The name in each `[Job(...)]`, such as `[Job("process-photo")]`,, and the namespace in `Run<MapWaitChildrenJobs>("photos")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ProcessPhoto` is stored with each job as its input, and `PhotoResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-photo-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
