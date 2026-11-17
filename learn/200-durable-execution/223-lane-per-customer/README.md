# How to apply each customer's commands in order while other customers keep moving

## Remember

- One job of a lane runs at a time, in enqueue order. The jobs behind the head read `blocked` until it finishes: succeeded, failed on its last attempt, or cancelled.
- A failing head keeps retrying in place and holds the lane. A paused head holds it until it is resumed.
- A head whose worker died holds the lane until recovery reclaims it: three to about seven minutes at the default heartbeat.
- A lane is order, not a condition: the next job runs after a failed or a cancelled head too.
- `[Job("sync-orders", Lane = "orders")]` on a definition puts every job of that definition in one lane.
- A concurrency key keeps jobs from overlapping and promises no order: an older job can be overtaken while the key is busy. [Limit how many jobs run at once for one key](../209-concurrency-key/) shows it.

## Check yourself

**Customer a's first command fails and is retried. What happens to a's second command meanwhile?**

- It runs, since the first one is not running right now
- It stays blocked until the first one finishes
- It is cancelled

<details><summary>Answer</summary>

It stays blocked until the first one finishes. A failing head keeps retrying in place and holds its lane, so the order holds across retries. Customer b's lane is not touched by it.

</details>
