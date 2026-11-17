# How to keep a repeated request from creating a second job

## Remember

- `handle.Action` says which happened: `EnqueueJobAction.Deduplicated` when the key matched a job already there, `Inserted` when this enqueue created it.
- The key prevents a second job row, not a second side effect. Execution stays at-least-once.
- Keys keep their case: `Order-1` and `order-1` are two keys, so lowercase an id yourself when its source ignores case.
