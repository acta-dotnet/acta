# How to save a record and start its job in one transaction

## Remember

- The transaction is yours. Acta never opens, commits, rolls back, disposes, or retries it, and any exception inside it means rolling back the whole of it.
- The answer of the enqueue is provisional until the commit and dead after a rollback. Keep or show the job's ref only once the commit has returned.
- No wake-up is published for an enqueue in a transaction, so a worker finds the job on its next poll after the commit.
- There is no `CallAsync` in a transaction and no handle: the job is invisible until the commit, so nothing could wait for it.
- The record's table has to live in the database Acta uses. Work for a separate database is handed over through an outbox.
- A request that may arrive twice still needs an idempotency key on the enqueue, as in [keep a repeated request from creating a second job](../010-idempotency-key/).
- The name in `[Job("fulfil-order")]` and the namespace in `Run<EnqueueInTransactionJobs>("enqueue-in-transaction")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `FulfilOrder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `fulfil-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**The transaction rolls back after the enqueue. What happens to the job?**

- It runs, and fails when it finds no order
- It never existed
- It stays ready until someone cancels it

<details><summary>Answer</summary>

It never existed. The job's row was written through the same transaction as the order's row, so the rollback took both, and no worker ever saw the job.

</details>
