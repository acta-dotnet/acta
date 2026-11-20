# How to route alerts to Slack from configuration

## Remember

- The name in `[Job("queue-depth-check")]` is stored with every job. Treat it as permanent once jobs exist under it: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `QueueDepthCheck` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `queue-depth-check-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
