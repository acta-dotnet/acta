# How to fan out over a list and merge the results

## Remember

- A child's name on the job reads is its `DeduplicationKey`: `{map}/{key}` for a map's child. List a parent's children with `acta.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent.JobRef))`.
- A failed or cancelled child never throws in the parent by itself. The wait answers an outcome: call `ThrowIfAnyFailed()` to fail with it, or branch on `Failed`.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.
- A group does not limit how many of its children run at once; that is `ConcurrencyLimit` on the child's definition.
