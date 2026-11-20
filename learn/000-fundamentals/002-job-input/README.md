# How to pass typed input to a job

## Remember

- The name in `[Job("print-name-tag")]` and the namespace in `Run<JobInputJobs>("job-input")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PrintNameTag` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `print-name-tag-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**`PrintNameTag` gets a new field. Which change keeps the jobs already stored readable?**

- A required field with no default
- An optional field with a default
- A new type for an existing field

<details><summary>Answer</summary>

An optional field with a default. The stored rows have no value for the new field, so it needs a default. A renamed, removed or retyped field needs a new job name beside the old one.

</details>
