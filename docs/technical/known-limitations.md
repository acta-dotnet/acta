# Known limitations

## Purpose

Known boundaries to review before using Acta in production-like environments.

## Stability status

Acta is at the release-candidate line: the public API, schema, and persisted codes are closing, and
release candidates change only for correctness, security, and documentation. The migration history
freezes at 1.0.0: from there, schema changes ship only as additive `Mnnn` migrations. Before the
release-candidate line the schema baseline (`M001`) could be re-cut per release, so a database
provisioned by a pre-rc build — or by the first rc.1 cut, which the certification round amended
once to widen the namespace id — may need one reprovision on the way in. Bootstrap compares the
baseline stamp recorded in the database against the one this build ships and refuses to start on a
mismatch, so a stale database fails loudly instead of taking a schema it was not built for; old
renumbered code values are intentionally incompatible, and there is no translation migration.

## Execution model

Acta jobs are at-least-once. A handler can run more than once after a crash, completion-write
failure, lease expiry, retry, or worker shutdown before completion is recorded.

Administrative actions can also make work run again. An operator restart deliberately re-arms a
job, and a database restore can rewind Acta state to a point where work appears runnable even if
external side effects already happened.

Acta does not provide deterministic workflow replay. The model is checkpoints, not replay: durable
slots record completed work and return stored results on re-entry, but the handler can re-enter from
the top.

Bounded waits carry the same at-least-once shape. A child-wait timeout's subtree cancellation is
initiated by the parent's resumed attempt: the expiry itself is durable, but a crash between the
expiry and the cancellation re-runs the cancellation on replay, and a parent that lands terminal
without ever replaying leaves its live descendants running — as parent death always has. An
expired wait slot is terminal for its name: any later wait on it resolves timed-out, which for the
unbounded overloads means the job is cancelled with `job.wait-timed-out` rather than parked on a
wait that can no longer complete. A deadline introduced by a code upgrade arms only when the job
is next claimed, so a job already suspended under the old unbounded code needs one operator
reschedule before the new bound takes effect.

Reclaiming a worker that died while a resolved timeout was being acted on re-arms the job on its
unchanged deadline without charging retry budget, so the replay lands the honest timed-out outcome
with the budget the feature promised untouched. The match is deliberately wide: any lease death
while the job's cached wake instant still points at the expired slot — which is the whole
resolving attempt, including work a `Try` handler does after the wait returned — re-arms uncharged
rather than burning budget on the feature's own promise. The cost: a worker that crashes
deterministically on every such replay loops without consuming budget. The loop is loud, twice
over — each uncharged reclaim projects a non-terminal failure into the job's own alert incident,
which re-notifies on the reminder interval for as long as the loop runs, and the job visibly
ping-pongs between suspended and claimed — and an operator cancel ends it at any phase. Bounding
it automatically would require persisting which overload armed the wait; rc.1 chooses the loud
unbounded loop over a budget charge that would break the promise for every ordinary crash.

No durable executor can guarantee exactly-once effects against arbitrary external systems. Acta gives
you at-least-once jobs, checkpointed durable steps, deduplication keys, and `AtMostOnce()` step
semantics; the application still owns external side-effect safety.

`AtMostOnce()` is for selected step bodies where duplicate execution is worse than an ambiguous
outcome. If the worker dies after Acta records the step start but before recording the outcome,
replay does not run the body again and the handler must reconcile whether the external side effect
happened.

The application owns side-effect safety. External systems such as payment providers, email
providers, webhooks, object storage, and partner APIs need deduplication keys, `AtMostOnce()`
placement, or reconciliation paths.

`Bulk` has relaxed completion durability. A crash can lose unflushed completion records and recovery
can re-run handler work that already finished in process. Use `Bulk` only for idempotent or safely
repeatable work.

A recurring job's failure is a rollover, not a terminal failure, and the quieter alert profiles wait
for a terminal one. `MaxAttempts` is the one-shot retry budget: a recurring slot never exhausts it,
re-arming for its next occurrence however many consecutive runs throw, which is deliberate — a nightly
job should not die permanently after three bad nights. But it means `AlertProfile.OnTerminal` and
`AlertProfile.Info`, which alert only on the terminal transition, stay silent for a recurring job that
throws or loses its lease. They still fire on the terminal shapes a recurring slot *can* reach, all of
which stop the whole slot: a handler that declares failure through `ctx.FailAsync(...)`, an uncaught
non-retryable exception, and an uncaught `StepInterruptedException` from a re-entered `AtMostOnce()`
step. A whole-job deadline is *not* among them — it lands the job `Cancelled` rather than `Failed`, and
no alert profile fires on a cancellation. **Choose `AlertProfile.OnFailure` for recurring work you want
to hear about**; it alerts on
each failure transition, and incident identity collapses a repeating nightly failure onto one row
rather than one per night.

## Ordering

Acta orders claims, not work. The claim scan reads ready rows by priority (highest first), then by
next-run instant, then by `JobId`, and that is a claim-time sort, not a queue discipline. `JobId` is
a stable tie-breaker inside one claim, not a multi-producer FIFO guarantee: database identities are
allocation order, not commit order.

Priority is strict, with no aging and no anti-starvation budget: while higher-priority ready rows
exist, lower-priority rows are not claimed, and a sustained high-priority flood can defer the
low-priority tail indefinitely. The intended remedy is a mechanism, not a knob: give bulk or
low-urgency workloads their own namespace, because each declared worker runs its own claim loop and
executor pool per namespace, so one namespace's flood cannot consume another's slots.

`ExclusiveKey` provides mutual exclusion, not ordering. While a worker holds a valid lease on the key,
no other job with that `(namespace, ExclusiveKey)` is admitted. The exclusion is as strong as the
lease and no stronger: a heartbeat renews it while the handler runs, so if that heartbeat stops — a
stalled process, a long pause, a partition — the lease can expire while the handler is still running
and another worker can admit the next job. That is the same at-least-once boundary described under
[Execution model](#execution-model), not a separate one.

Admission order is unspecified: under sustained arrivals that keep a key held, an older job can be
repeatedly overtaken, and Acta does not bound its wait. Use it for exclusive *unordered* work. There
is no fairness, aging, or queue-position guarantee for a contended key, and none is planned; a job
that needs a bounded wait needs a different design.

Three levels are available, and only the third one orders anything.

**Best-effort serial dispatch under restricted conditions.** One worker process for the namespace,
`MaxConcurrentExecutors = 1`, `ClaimBatchSize = 1`, equal priority, and jobs that are due
immediately. This yields serial execution, not strict FIFO: retries, delayed eligibility, priority
changes, and operator actions move a row's next-run instant and reorder what is claimed next. Acta
does not enforce that only one process claims a namespace, so the single-process condition is an
operational promise, not a runtime invariant.

**Exclusive unordered work.** `ExclusiveKey`, under the contract above.

**Strict ordered processing.** A durable coordinator or chain job that releases item N+1 only after
item N has reached the required outcome. Acta ships no built-in ordering key for this; you write the
coordinator. The cost is head-of-line blocking, so the design needs an explicit policy for a poison
item that never reaches its outcome.

## Contract evolution

Contract evolution requires discipline. Jobs are durable rows, and old rows may execute after a new
deploy. Keep the same job name only when old stored payloads still deserialize and run correctly;
use versioned job names for incompatible changes.

Acta's contract drift guard is not a full JSON schema compatibility checker. It catches
definition-level input/output type and payload-format changes; the application owns JSON wire
compatibility.

See [`contract-evolution.md`](../guide/contract-evolution.md).

## Dashboard and API exposure

Dashboard auth is the host application's responsibility. Acta ships no login system.

The dashboard and JSON API are local-only by default, and controls are disabled by default. Remote
exposure requires `LocalOnly = false` plus host authorization through `ConfigureEndpoints`; mapping
without either throws at startup unless `UnsafeAllowAnonymousRemoteAccess = true` is set explicitly.

Behind a reverse proxy on the same host, do not rely on `LocalOnly`; use real authorization.

## Storage and providers

SQLite has concurrency limits compared with server databases. Use SQL Server or PostgreSQL for
distributed multi-worker deployments.

On SQLite the `Bulk` execution profile degrades to `Direct`, silently. Group-committed completions
need a batched-completion routine, and the SQLite provider has none, so the completion buffer is
never created: the worker runs `Direct`'s combined claim-execute loop and commits every completion per
job. Nothing logs the downgrade and no setting rejects it. The degradation is complete: a `Bulk`
worker on SQLite gets `Direct`'s behavior on both axes, including the relaxed commit fsync
(`PRAGMA synchronous = NORMAL`) that `Direct` selects there — you asked for relaxed durability in
exchange for throughput, and relaxed durability is the half SQLite can honor. The result is correct
and durable to `Direct`'s standard; it simply performs like `Direct`, not like `Bulk`.

On SQLite, a `JobId` returned by a transactional enqueue that then rolls back is handed out again.
`sqlite_sequence`, which backs `AUTOINCREMENT`, is an ordinary table, so the counter is rolled back with
the transaction that advanced it and the next insert receives the same id. PostgreSQL and SQL Server
allocate from non-transactional sequences, which burn the value instead. Acta's own contract is the same
on all three: a transactional enqueue that rolls back leaves no durable row. What differs is what the
returned identity means afterwards — an application that holds `JobEnqueueOutcome.JobId` across a
rollback and looks it up later can, on SQLite, read a *different* job that has since been given that id.
Hold `JobRef` instead. It is a UUIDv7 minted in your process before the row is written, so you have it
in advance and nothing else will ever carry it, whereas a `JobId` is only meaningful for an enqueue that
committed.

A namespace id is 32 bits, so one database can allocate around 2.1 billion namespaces over its
whole lifetime. Ids are never reclaimed — deleting a namespace does not return its id, so the
ceiling counts every namespace ever created rather than the number that currently exist — but at
this width that is an accounting note, not a budget: registration allocates an id only for a
genuinely new namespace name (a worker restart re-registering an existing namespace allocates
nothing), and namespaces are created deliberately, one per bounded context, not per request.
SQLite's `namespaces.id` is a 64-bit `AUTOINCREMENT`; 2,147,483,647 is the portable limit.

Microsoft.Data.Sqlite (verified in 10.0.11 and on its `main`) carries an unsynchronized enumeration
in `SqliteConnection.Deactivate`: returning a pooled connection walks the custom-function dictionary
with no lock, so a concurrent reclaim can fault inside the driver. A plain user never sees it — the
dictionary is empty unless `CreateFunction` was called — but Acta registers its `acta_blob` and
`acta_error` functions on every open, which makes the upstream race reachable. Acta makes it
reachable; it does not cause it. Observed only as a rare test-suite fault under heavy cross-process
parallelism, never reproduced in isolation, and rc.1 deliberately changes nothing for it: the
fault sits in the driver's pool, and `Pooling=false` would trade it for a fresh native open per
connection, a real cost that would need benchmarking first.

Acta requires no ambient isolation level — every mutation is guarded by explicit lock hints or a
compare-and-swap predicate, so job correctness does not depend on snapshot isolation — but it
provisions and is tested under one configuration per engine: READ_COMMITTED_SNAPSHOT on SQL Server,
plain Read Committed on PostgreSQL, WAL journal mode on SQLite. Change those and correctness holds
while operability degrades: without RCSI, dashboard and list reads take shared locks and trade
blocking with the claim path; reverting SQLite to a rollback journal reintroduces a global write
lock. An application-imposed Serializable level makes the signal-wait arming race surface as a
serialization retry rather than resolving in place — safe, but untested. Only the preferred
configuration is exercised by the conformance suites.

SQL provider behavior should be tested under your workload, including claim pressure, long-running
handlers, retries, schedule load, alert delivery, retention sweeps, and backup/restore drills.

Large or sensitive payloads should live outside Acta tables. Store a reference in the job input and
keep the body in blob/file/object storage.

## Category boundaries

Acta is not a message bus. It does not provide a general-purpose transport abstraction, pub/sub
fabric, topic model, or Kafka-style streaming.

Acta is not a workflow SaaS. It does not provide a hosted orchestration control plane, login system,
tenant-admin product surface, BPMN, or visual workflow modeling.

Acta is not a streaming platform. It records durable job lifecycle and operator state; it is not
for ordered event streams, consumer groups, retention topics, or replaying high-volume logs.
