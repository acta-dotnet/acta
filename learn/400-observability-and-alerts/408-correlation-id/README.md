# How to carry a correlation key from parent to child

## Remember

- `"step-1"` is how `run-pipeline` finds its child when it runs again. A renamed child is new work.
- A parent and its children are each stored under their own job name. The names in `[Job("run-pipeline")]` and `[Job("process-step")]`, and the namespace in `Run<CorrelationKeyJobs>("correlation-id")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `RunPipeline` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `run-pipeline-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
