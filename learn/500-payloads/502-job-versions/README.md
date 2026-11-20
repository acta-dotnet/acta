# How to change a job's input without breaking the jobs already stored

## Remember

- A job enqueued before a deploy can run after it. `SendInvoice` is stored with each job, so the new code has to read what the old code wrote.
- Keep the name `send-invoice` for a change the stored rows survive: an optional, nullable or defaulted field, such as `Note`.
- Use a new versioned name, `send-invoice-v2`, when the stored rows would be misread or rejected: a renamed, removed or retyped field, a changed meaning, or required data they cannot supply, such as `Currency`.
- Put the new version right under the old one. From then on there are two to maintain, until the old one's jobs have finished.
- Keep the old handler registered until none of its jobs can still run, wait or be restarted. Dropping it cancels nothing: its jobs stay Ready with no handler to run them.
- The name in `[Job("send-invoice")]` and the namespace in `Run<JobVersionsJobs>("job-versions")` are stored with every job too. A version is a new name beside the old one, never a rename.
- Inside a handler, a step, signal, timer, variable and child name is a key into what the job has stored. A renamed step runs again.

## Check yourself

**A `send-invoice` job is waiting for its next retry when the build with `send-invoice-v2` is deployed. Which handler runs it?**

- The `send-invoice-v2` handler, since it is the newest
- The `send-invoice` handler, since the job's name is stored with the job
- Neither, until an operator moves the job to the new name

<details><summary>Answer</summary>

The `send-invoice` handler, since the job's name is stored with the job. That is why the old handler stays registered: the stored jobs still ask for it by name.

</details>

**A stored row has no `Note`. What does the handler read?**

- The job fails, since a field is missing
- `null`, the field's default
- An empty string

<details><summary>Answer</summary>

`null`, the field's default. An optional field with a default is a change the stored rows survive, so the job keeps its name.

</details>
