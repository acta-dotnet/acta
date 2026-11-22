# How to integrate Acta with Orleans

## Remember

- Each does what it is good at. The grain is the live object: it has an address, owns its state, and answers from memory. Acta runs and records the work the grain starts: its steps, its retries, and what happened to it.
- The grain hands the work over and returns. A grain that waited for the job inside the call could not take the job's own call back, since by default it answers one call at a time.
- The enqueue carries an idempotency key. The grain's state and the job are saved separately, so a call that is repeated must find the job the first call made. The grain answers with the job's ref, and the ref is how the rest of the application reaches the job: it is one word, unique across namespaces, so the payment provider's confirmation needs nothing else to find it.
- The grain method a job calls must be safe to receive twice. The call is repeated if the job stopped after the grain was told and before the step was recorded, so the wallet adds a top-up once, by its id.
- A step that finished is not run again. The receipt fails on the first attempt and the job runs a second time: the payment is requested once, the job waits once, and the wallet is told once.
- What the job did is rows in the database: the job, each step with its outcome and each attempt with its error, read with a plain query. The wallet's balance is in memory and in the storage the grain writes to.
- Where your own record lives in the database Acta uses, the enqueue can join your transaction, and the record and its job commit together: [Save a record and start its job in one transaction](../../000-fundamentals/029-enqueue-in-transaction/). A grain's storage is its own, so here the key does that work.
- A grain can do this alone, and where that is enough it should: it saves how far the top-up got and takes the provider's confirmation as another call. To retry by itself, or to notice a confirmation that never came, it also needs a reminder and its own count of attempts. A job brings those: recorded steps, a wait, a retry policy, and a history an operator can read.
- This program runs the silo and the worker in one application, with localhost clustering and the grain's state in memory storage. It shows nothing about a worker in another process or a cluster of several silos.
- `"request-payment"`, `"add-to-balance"` and `"send-receipt"` are the keys to each step's stored result, and `"payment-confirmed"` names the signal the job waits on. Rename `"request-payment"` and the provider is asked again for a payment it already took.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("top-up-wallet")]` and the namespace in `Run<WalletsJobs>("wallets")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `TopUpWallet` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `top-up-wallet-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**The wallet's `TopUpAsync` is called a second time with the same top-up while its job still exists. What does Acta do?**

- It enqueues a second job
- It answers with the first job, since both calls carry the same idempotency key
- It refuses the call with an error

<details><summary>Answer</summary>

It answers with the first job, since both calls carry the same idempotency key. The grain keeps no record of its own that it already asked, so the key is what makes the repeated call safe.

</details>

**Why does `TopUpAsync` return as soon as the job is enqueued and not wait for the payment?**

- The grain cannot read a job's result
- A grain that waited for the job could not take the job's call back
- Jobs have no results

<details><summary>Answer</summary>

A grain that waited for the job could not take the job's call back. By default a grain answers one call at a time, so `AddToBalanceAsync` would wait behind the `TopUpAsync` that is waiting for it.

</details>
