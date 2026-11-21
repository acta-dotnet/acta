# How to find the child job that handles one item

## Remember

- A child's name on the job reads is its `IdempotencyKey`: the call's name for `EnqueueChildAsync`, `{map}/{key}` for a map's child.
- Keys keep their case: `Invoice/7` and `invoice/7` are two keys, so build the key the same way where you start the child and where you look for it.
- The list answers a page at a time, fifty jobs unless `PageSize` says otherwise and a hundred at most. A parent with more children is read page by page, as in [list and page through jobs](../../400-observability-and-alerts/409-operator-queries/).
- A failed child does not fail its parent by itself, so a parent that reads `succeeded` can still have a child that failed. [Handle child jobs that fail](../219-child-failure-outcomes/) shows the parent's side.
- `"invoice"` is how `send-invoices` finds the child for each item. A renamed child is new work: every invoice is sent again.
- A parent and its children are each stored under their own job name. The names in `[Job("send-invoices")]` and `[Job("send-invoice")]`, and the namespace in `Run<FindChildJobJobs>("find-child-job")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendInvoices` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-invoices-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**A parent mapped invoices 5, 6 and 7 under the name `invoice`. Which idempotency key does the child for invoice 6 carry?**

- `6`
- `invoice/6`
- `send-invoice/6`

<details><summary>Answer</summary>

`invoice/6`. The name the map was given comes first, then the item's key. The job's own name, `send-invoice`, is not part of it.

</details>
