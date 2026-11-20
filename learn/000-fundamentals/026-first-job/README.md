# How to build the first-job guide's app

## Remember

- The names in `[Job("ship-order")]` and `[Job("print-label")]`, and the namespace in `Run<ShippingJobs>("shipping")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ShipOrder` is stored with each job as its input, and `ShipmentLabel` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ship-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
