# How to fan out to child jobs with Map or WaitChildren

## Remember

- A child's name on the job reads is its `DeduplicationKey`: the call's name for `EnqueueChildAsync`, `{map}/{key}` for a map's child.
- A group waits for every child. It never fails fast and never cancels siblings.
- The parent wakes only when the child it is waiting on lands, so it can run several times before the group is done. Keep the work before the wait in a step.
