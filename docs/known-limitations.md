# Known limitations

Known boundaries to review before using Acta in production-like environments.

## Stability status

Acta is at 1.0.0-rc.6, a release candidate. It re-cut the schema ([release notes](./release-notes.md)), so
every database an earlier release candidate provisioned is dropped and reprovisioned: no translation
migration exists, and nothing in a database says which cut it came from. From 1.0.0 the public API,
schema, and persisted codes are frozen, and the migration history with them: schema changes ship only
as additive migrations and the baseline is never re-cut, so a database provisioned by a 1.x build
upgrades by migration alone.

Every baseline statement is existence-guarded, so a later build's re-cut baseline applied over a pre-1.0
database would change nothing and keep, for example, `events.actor` as `varchar(128)` on SQL Server,
folding an operator's non-ASCII name to `?`. Startup and migration apply refuse such a database instead,
naming the baseline its history holds. A history row is still only a migration name and when it was
applied, never a fingerprint of the schema, so an operator who adapts the applied script keeps the
recorded row and carries the difference themselves.

Reprovisioning is a manual step, and it is destructive: there is no upgrade path from a database
provisioned before 1.0.

## Execution model

- **At-least-once.** A handler can run again after a crash, a lease expiry, a failed completion write, or a shutdown, and a restart or a database restore can re-run work whose external effects already happened. No durable executor makes external effects exactly-once; the application owns that, with deduplication keys, durable steps, `AtMostOnce()`, or reconciliation.
- **Checkpoints, not replay.** A handler re-enters from the top; completed durable slots return their stored results.
- **A failing completion write repeats until it lands or the worker stops**, logging Warning, then Error. A database outage longer than the lease lets recovery reclaim the job and run it again, finished or not. A failing database stops intake instead of growing memory.
- **SQLite pickup can wait a Windows timer tick.** A write that meets another commit holding SQLite's lock waits in SQLite's busy handler, which sleeps in whole milliseconds that Windows rounds up to a 15.6 ms tick, so one job at a time picks up in about 20 ms where the server providers take 3.
- **A dead worker's jobs come back within two leases**, six minutes at the default heartbeat.
- **One job row takes about 700 million state changes**, about three per run. A recurring schedule fires at most once a second, which keeps it inside the limit for decades, but a job that reschedules itself without pause at a hundred runs a second reaches it in about three months; a loop that long should enqueue a successor job instead.
- **A worker can claim a job it has no handler for.** It hands the job back once and stops claiming that definition; with no worker carrying it, the job waits Ready until a build with the handler returns or an operator retires the definition.
- **An override reaches workers on their next heartbeat.** Two edits of one namespace committed in the same instant in reverse order can leave one unseen until the next edit.
- **One rate meter is one row lock**, about a thousand admissions a second cluster-wide. A turn under a quarter second away is waited out holding the executor, and a worker that dies in that wait costs the job one attempt.
- **A meter can run at two rates** when a deploy moves a definition onto another meter or a retune races a worker start. Workers warn, and one override on any participant fixes it.
- **A provider error at admission bounces the attempt** with `job.unclassified`, spending no budget and raising no alert; a concurrency slot it took without an answer stays taken for up to one lease.
- **A wait deadline added in a code upgrade arms at the job's next claim**, so a job already suspended under the old code needs a reschedule.
- **A descendant walk that stops early leaves the rest of the tree running.** It stops when the process walking it dies, or when an operator's cancel hits a database error after its caller stopped waiting. Cancelling the job again walks them.
- **A permanent slot keeps what it adds on each fire.** Its slots and the children it started stay as long as the job, which lives as long as its schedule, so a child or a step named per period is never purged. Declare the definition `ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence` for work that should drain with retention ([developer guide](./developer-guide.md#delivery-and-capacity)).
- **A `PerOccurrence` definition declares no lane and no deadline**; the generator refuses both, as it does for a permanent slot.
- **An orphaned per-occurrence schedule's row stays** until its definition is retired: there is no slot whose purge removes it, and no schedule delete verb yet.
- **On SQL Server, a child enqueued in a transaction still open when its parent is cancelled escapes the cancel.** The cancel does not wait for the uncommitted child, which runs once the transaction commits. Cancelling the parent again walks it.
- **A worker that crashes on every replay of a timed-out wait loops without spending budget.** The job re-alerts on every reminder, and an operator cancel ends it.
- **`OnTerminal` and `Info` never alert on a recurring job's ordinary failures**, which return the slot to `Idle` instead of landing Failed. Use `OnFailure` for recurring work.
- **The alert projector assumes the database clock never steps backwards.** After a step, failures stamped below the cursor are missed. Repair by setting the `alerts-cursor` variable of the namespace's `sys.alerts` slot in `checkpoints` to `{"Ticks":<UTC ticks of an instant before the step>,"EventId":0}`, no further back than `AlertRetention`.
- **A writer that freezes past twice the command timeout** can commit an event the projector already passed, which is then missed (never wrong). Keep command timeouts the same across the fleet.
- **A requeue command can requeue a row a second time.** A relay pass that stalls between planning the command and requeueing its rows, while another pass finishes it and the row is quarantined again, requeues that row once more and records no event for it. The row retries as the operator asked; nothing else changes.

## Ordering

- **Claims are ordered, work is not.** There is no global FIFO, and a retry can be overtaken by later jobs.
- **Priority is strict, with no aging.** A sustained high-priority flood defers the rest; put bulk work in its own namespace.
- **A `ConcurrencyKey` excludes, it does not order.** Waiters are admitted in no order with no bound on the wait, and the exclusion lasts only as long as the lease.
- **Lanes order strictly, at a cost:**
  - a head that retries, waits, or is paused holds its lane, and a poison head holds it until its attempts run out or it is cancelled;
  - a lane advances past a head that failed for good or was cancelled;
  - one job per lane runs at a time;
  - an open transaction that enqueued into a lane holds it until it ends;
  - a laned enqueue inside your transaction needs READ COMMITTED: it is refused under PostgreSQL REPEATABLE READ or SERIALIZABLE and SQL Server SNAPSHOT;
  - a head whose definition no running worker carries stalls its lane;
  - a cycle of waits across lanes is invisible to Acta; give such waits a timeout;
  - a restarted job runs right after the current head, out of its original place;
  - cancelling or recovering a running head can let the next one start while the old handler still runs.
- **Outbox lanes order sequential producers only.** Rows staged into a lane in sequence reach the ledger in staging order, but producer transactions open at the same time have no order between them: a row whose transaction commits first can be relayed ahead of an older row still uncommitted.

## Contract evolution

- **The drift guard checks type names and payload formats, not JSON compatibility.** Wire compatibility is the application's.
- **A worker excludes a definition it has no handler for only for its own lifetime.** A long run of such jobs slows its claims until the definition is retired or its build returns.
- **Retiring a definition cancels its parked jobs but not their descendants.**
- **A worker learns of a retire on its next heartbeat**, so an attempt that starts in that gap still runs once. A job its handler paused stays Paused until it is resumed or cancelled.

## Dashboard and API exposure

Authentication is the host's: Acta ships none. The surface is local-only by default with controls off, and behind a reverse proxy on the same host `LocalOnly` protects nothing.

## Storage and providers

SQLite has concurrency limits compared with server databases. Use SQL Server or PostgreSQL for
distributed multi-worker deployments.

On SQLite, a `JobId` returned by a transactional enqueue that then rolls back is handed out again.
`sqlite_sequence`, which backs `AUTOINCREMENT`, is an ordinary table, so the counter is rolled back with
the transaction that advanced it and the next insert receives the same id. PostgreSQL and SQL Server
allocate from non-transactional sequences, which burn the value instead. Acta's own contract is the same
on all three: a transactional enqueue that rolls back leaves no durable row. What differs is what the
returned identity means afterwards — an application that holds `EnqueueJobResponse.JobId` across a
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
SQLite's `namespaces.namespace_id` is a 64-bit `AUTOINCREMENT`; 2,147,483,647 is the portable limit.

Microsoft.Data.Sqlite (verified in 10.0.11 and on its `main`) carries an unsynchronized enumeration
in `SqliteConnection.Deactivate`: returning a pooled connection walks the custom-function dictionary
with no lock, so a concurrent reclaim can fault inside the driver. A plain user never sees it — the
dictionary is empty unless `CreateFunction` was called — but Acta registers its `acta_blob` and
`acta_error` functions on every open, which makes the upstream race reachable. Acta makes it
reachable; it does not cause it. Observed only as a rare test-suite fault under heavy cross-process
parallelism, never reproduced in isolation, and 1.0 deliberately changes nothing for it: the
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

A recurring job that an operator restarts after a terminal failure does not auto-resolve its open
alert at the `Failures` or `Off` recording levels: the restart zeroes the failure count, so the next
successful fire is not captured as a recovery. Resolve the incident manually, or run the definition
at `Audit`, where every success is recorded and resolves it. One-shot jobs auto-resolve after a
restart at every level.

Large or sensitive payloads should live outside Acta tables. Store a reference in the job input and
keep the body in blob/file/object storage.

## Category boundaries

Acta is not a message bus. It does not provide a general-purpose transport abstraction, pub/sub
fabric, topic model, or Kafka-style streaming.

Acta is not a workflow SaaS. It does not provide a hosted orchestration control plane, login system,
tenant-admin product surface, BPMN, or visual workflow modeling.

Acta is not a streaming platform. It records durable job lifecycle and operator state; it is not
for ordered event streams, consumer groups, retention topics, or replaying high-volume logs.
