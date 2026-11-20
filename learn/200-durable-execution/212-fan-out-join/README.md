# How to fan out over a list and merge the results

## Remember

- A child's name on the job reads is its `DeduplicationKey`: `{map}/{key}` for a map's child. List a parent's children with `acta.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent.JobRef))`.
- A failed or cancelled child never throws in the parent by itself. The wait answers an outcome: call `ThrowIfAnyFailed()` to fail with it, or branch on `Failed`.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.
- A group does not limit how many of its children run at once; that is `ConcurrencyLimit` on the child's definition.
- `"chunk"` is how the parent finds the children it already started. A renamed child is new work: every chunk is added up again.
- A parent and its children are each stored under their own job name. The names in `[Job("add-numbers")]` and `[Job("sum-range")]`, and the namespace in `Run<FanOutJoinJobs>("fan-out-join")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `AddNumbers` is stored with each job as its input, and `SumResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `add-numbers-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
