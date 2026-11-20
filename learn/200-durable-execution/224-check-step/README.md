# How to ask the provider before repeating a step after a crash

## Remember

- The check is asked only when the step was started and never recorded, in the same attempt or a later one. On a first run it is not asked.
- Look the effect up by a key taken from this job's input, and answer only for this step's own effect. A wrong "found" skips the body and stores that value for good; a miss only runs the body again.
- One outside effect per step, and no wait inside its body: a wait there fails the job at once. Put the wait between steps.
- The check must change nothing: it can be asked more than once.
- This narrows the window in which a step repeats. It is not exactly-once: an attempt whose lease expired can still be mid-call when the next one asks, and a provider can answer late. An idempotency key at the provider is the stronger guard, and an at-most-once step is the one for an effect that can be neither asked about nor repeated: [Refuse to repeat a side effect after a crash](../220-at-most-once-step/).
- `"charge-card"` and `"book-carrier"` are the keys to each step's stored result, and `"packed"` names the signal the job waits on. Rename `"charge-card"` and the step is a new one: its check is not asked and the card is charged again.
- The name in `[Job("ship-order")]` and the namespace in `Run<CheckStepJobs>("check-step")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `ShipOrder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `ship-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**The first run charges the card and dies before Acta records the step. What does the next run do?**

- It charges the card again, since the step has no recorded result
- It asks the bank for the order's charge, records the charge it finds, and does not charge again
- It fails the job, since the outcome is unknown

<details><summary>Answer</summary>

It asks the bank for the order's charge, records the charge it finds, and does not charge again. The step was started and never recorded, so the check is asked before the body; a value it answers becomes the step's result.

</details>

**Why does the wait for `"packed"` sit between the two steps instead of at the end of the charge step's body?**

- A wait inside a check step's body fails the job at once: after the wait the check would find the charge and skip the rest of the body
- A wait inside any step's body is refused
- A wait inside a step's body would charge the card again

<details><summary>Answer</summary>

A wait inside a check step's body fails the job at once: after the wait the check would find the charge and skip the rest of the body. A wait inside an ordinary step's body works, since its resume runs the body again up to the wait.

</details>
