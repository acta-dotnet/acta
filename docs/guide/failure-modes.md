# What happens if…

## Purpose

Failure-mode answers for operators and handler authors. Start a single-job investigation with
`<app> jobs explain <job-ref>` or the dashboard Explain panel; use this page to understand the
expected transition and the application responsibility behind it.

## …a worker crashes before starting the handler?

The job can remain `Dispatched` under the worker lease. When the lease expires, `sys.recovery`
returns it to `Ready` while retry budget remains, or marks it `Failed` when the budget is exhausted.
No handler side effect should have occurred because execution never started.

## …a worker crashes while the handler is running?

The lease eventually expires and recovery makes the job runnable again within budget. Bare handler
code may run again. Succeeded durable steps return their stored results; an in-progress step whose
outcome was not recorded follows its configured semantics.

External systems are not rolled back. Use deduplication keys, reconciliation, or carefully selected
`AtMostOnce()` steps when duplicate execution is worse than an ambiguous outcome.

## …the external call succeeds but Acta cannot record completion?

The durable row still looks incomplete, so a later attempt can repeat the call. This is the central
at-least-once boundary. Pass a stable business deduplication key to the external system or reconcile its
state before repeating the side effect.

## …the database is unavailable?

Workers cannot claim, heartbeat, checkpoint, or complete durable work without SQL. A handler already
inside an external call may still affect that external system before its next Acta write fails.
After SQL returns, inspect expired leases and let recovery reconcile them; do not assume database
recovery also reversed external effects.

## …a signal arrives before the handler starts waiting?

The signal value is stored in the named checkpoint. When the handler later calls
`WaitSignalAsync` with the same name, it consumes the stored value without suspending. A signal sent
to a terminal job is rejected; a signal sent to a paused job is stored but does not resume the job
until the job itself is resumed.

## …a sleeping or signal-waiting job has no worker?

The wait occupies no executor. A timer remains durable until due; a signal wait remains durable
until raised. Once the job becomes `Ready`, a live worker registered for the namespace and definition
must claim it. Explain and the Workers page show whether that capacity exists.

## …the application deploy removes a handler that queued jobs still need?

Stored rows keep their durable job name. They are not rewritten to another handler. Keep the old
definition registered until runnable/waiting rows drain or expire, or deploy a compatible/versioned
migration plan. Search retained failed jobs too: an operator restart can make them runnable again.

## …a checkpoint name changes during a deployment?

The new name is a different durable slot. A step can repeat, a variable can appear absent, or a
signal can wait on a name that old producers never raise. Follow
[Contract evolution § durable slot evolution](./contract-evolution.md) and treat the change as a data
migration, not a refactor.

## …the service is down across scheduled occurrences?

The schedule's misfire policy decides the cursor when the schedule reloads or resumes. `Skip` moves
past missed occurrences; `CatchUpOnce` produces one coalesced catch-up execution. Neither policy
creates one historical job per missed period. See [Schedule operations](./schedule-operations.md).

## …an operator restarts a failed or cancelled job?

Restart re-arms the same job and keeps its input and event history. Succeeded durable steps still do
not rerun unless state was explicitly reset, but bare handler code and incomplete steps can run
again. Fix or understand the original cause before confirming restart.

## …an operator purges a job?

Purge permanently removes the terminal job, result, checkpoints, steps, tags, alerts, and prior
events, then emits a standalone `job.purged` audit event. It rejects non-terminal jobs and parents
with child jobs. See [Operator guide § retention and purge](./operator-guide.md).

## …the dashboard backend goes offline?

The dashboard shows its offline banner and cached rows may remain visible, but controls and fresh
reads cannot complete. Treat SQL/API availability as unknown until the backend recovers; do not infer
that a clicked action applied unless its response said `applied` and the refreshed timeline confirms
the event.

## …an alert cannot be delivered?

The alert row remains queryable independently of transport delivery. Inspect `delivery_status`, the
configured channel registry, and worker logs. Acknowledge means an operator has seen the alert;
resolve means the incident is considered settled. Neither action changes the underlying job.

## …the database is restored from backup?

Acta state rewinds to the backup point; external systems do not. Work that completed after the backup
can appear runnable again, and signals or operator actions after the backup can disappear. Run a
reconciliation before releasing workers, using stable external deduplication keys and the restored
timeline as evidence, not as proof that external effects did or did not happen.

For symptom-driven commands and SQL, continue with [Troubleshooting](./troubleshooting.md). For
deployment drills, see [Production guide](./production.md).
