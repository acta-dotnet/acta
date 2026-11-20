# How to start a child job in another namespace

## Remember

- `"charge"` is how `place-order` finds the payment it already started in the other namespace. A renamed child is new work: the payment is charged again.
- A parent and its children are each stored under their own job name. The names in `[Job("place-order")]` and `[Job("charge-payment")]` and the namespaces `"storefront"` and `"billing"` are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PlaceOrder` is stored with each job as its input, and `OrderConfirmation` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `place-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
