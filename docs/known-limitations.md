# Known limitations

Known boundaries to review before using Acta in production-like environments.

## Stability status

Acta 1.0 is in release candidates. Since rc.11 a release candidate changes the schema with a migration on
rc.10's baseline, the way every 1.x release will on 1.0.0's, and 1.0.0 itself may still re-cut the baseline
([release notes](./release-notes.md)). A database provisioned under another baseline is dropped and provisioned
again: no migration translates one baseline into another. From 1.0.0 the public API,
schema, and persisted codes are frozen, and the migration history with them: schema changes ship only
as additive migrations and the baseline is never re-cut, so a database provisioned by a 1.x build
upgrades by migration alone.

Every baseline statement is existence-guarded, so a later build's re-cut baseline applied over a pre-1.0
database would change nothing and keep, for example, an rc.9 database's 32-bit
`jobs_state.execution_number` and its `events` table without the `actor` column this build writes.
Startup and migration apply refuse such a database instead, naming the baseline its history holds. A
history row is still only a migration name and when it was
applied, never a fingerprint of the schema, so an operator who adapts the applied script keeps the
recorded row and carries the difference themselves.

Reprovisioning is a manual step, and it is destructive.

## Execution model

Execution is at-least-once, and a handler re-enters from the top with its completed steps returning their stored results. That is the model, stated in full under [Execution semantics](./developer-guide.md#execution-semantics); the entries below are the limits inside it.

- **A failing completion write repeats until it lands or the worker stops**, logging Warning, then Error. A database outage longer than the lease lets recovery reclaim the job and run it again, finished or not. A failing database stops intake instead of growing memory.
- **A handler that ignores its cancellation token cannot be stopped.** It keeps its lease, its concurrency slot, and its locks until it returns, so the job and every job behind those keys wait for it; when it returns normally the job is recorded Succeeded, however late. Once it has run more than half a lease past its execution timeout, the worker logs an error naming the job. Restarting the worker frees the job: recovery reclaims it and it runs again.
- **SQLite pickup can wait a Windows timer tick.** A write that meets another commit holding SQLite's lock waits in SQLite's busy handler, which sleeps in whole milliseconds that Windows rounds up to a 15.6 ms tick, so one job at a time picks up in about 20 ms where the server providers take 3.
- **A checkpoint and a schedule's state each take about 2.1 billion writes.** Their versions are the 32-bit counters left. A variable, signal, or progress row written that often fails that job's own write and no other. A permanent job's firing record and its schedule's state are written twice a firing, by statements that work on a batch, so a worn one would fail its namespace's admission pass; a schedule fires at most once a second, which puts that decades away.
- **A worker can claim a job it has no handler for.** It hands the job back once and stops claiming that definition for its own lifetime; with no worker carrying it, the job waits Ready until a build with the handler returns or an operator retires the definition, and a backlog of such jobs slows that worker's claims meanwhile.
- **Applications that share a database must share a `HeartbeatInterval`.** A recovery pass marks dead every worker silent for longer than its own `WorkerDeadAfter`, in every namespace, so the application with the shorter interval marks the other's workers dead between their beats, and each next beat revives them: a `worker.died` and a `worker.started` every time. Jobs are unaffected, since a lease is judged by its own expiry.
- **A rate limit paces admissions, not starts.** At a steady rate any `T` seconds hold at most `R*(T+W)+B` admissions, `W` being the longest a booked turn is honoured late: one second, or one interval when that is longer. A turn more than fifteen minutes late may be swept, so a job that waits that long for a worker, on a meter slower than one per fifteen minutes, waits for a new turn. A delayed reservation can be admitted next to an on-time one, so `RateLimit` alone is not a hard quota for a partner API that rejects bursts.
- **One rate meter is one row lock**, about a thousand admissions a second cluster-wide. A turn under a quarter second away is waited out holding the executor, and a worker that dies in that wait costs the job one attempt.
- **A handler lock stops at the namespace.** `RunWithLockAsync` keys are scoped to the job's namespace, as concurrency slots and rate meters are, so two namespaces never serialize on one key. Jobs that share one external single-writer resource belong in one namespace.
- **A meter can run at two rates** when a deploy moves a definition onto another meter or a retune races a worker start. Workers warn, and one override on any participant fixes it.
- **A provider error at admission bounces the attempt**: it is rescheduled with a message that says so and no reason, spends no budget, and raises no alert; a concurrency slot it took without an answer stays taken for up to one lease.
- **A wait deadline added in a code upgrade arms at the job's next claim**, so a job already suspended under the old code needs a reschedule.
- **A wait inside a step runs the step's body again up to the wait** on every resume, so what the body does before the wait must be idempotent. An at-most-once body cannot wait at all: the call throws.
- **Compensating on `ctx.IsLastAttempt` is best effort.** A worker crash or a lost lease on the last attempt skips the catch block, and a reclaim can spend the budget and fail the job with no handler code running. Make the compensation an idempotent step, and run work whose compensation must happen as a child job: the parent compensates when the child lands Failed, since `WaitChildAsync` survives crashes.
- **A descendant walk that stops early leaves the rest of the tree running.** It stops when the process walking it dies, or when an operator's cancel hits a database error after its caller stopped waiting. Cancelling the job again walks them. A restart of the job also ends its walk, and the restarted job keeps the descendants the walk had not reached.
- **A permanent job keeps what it adds on each fire.** Its steps and the children it started stay as long as the job, which lives as long as its schedule, so a child or a step named per period is never purged. Declare the definition `ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence` for work that should drain with retention ([delivery and capacity](./scheduling.md#delivery-and-capacity)).
- **A `PerOccurrence` definition declares no lane**; the generator refuses one on any scheduled definition (ACTA0121). A permanent job declares no deadline either, which the deploy refuses.
- **An orphaned per-occurrence schedule's row stays for good**, after its definition is retired too: a schedule row is deleted only with its permanent job, a per-occurrence schedule has none, and no verb deletes a schedule.
- **On zone data that gives a zone negative daylight time, an occurrence in a past year is computed by the zone's present rule.** Europe/Dublin is the one such zone in force, and Ubuntu 26.04 ships it in that form; Ubuntu 24.04 and Windows do not. There Acta computes the zone the ordinary way round from its present rule, and keeps that only while it gives the zone's own clock from the current year on. Dublin's rule dates from 1996, so a preview or a backlog that reaches before 1996 can read a local time near a clock change an hour off. A schedule from 1996 on fires as it does on any other host.
- **A time-zone rule change misplaces one occurrence of a zoned cron.** After a host's time-zone data changes a zone's rules (a tzdata or Windows update), a cron schedule in that zone whose next occurrence was computed under the old rules fires that occurrence at the old instant, an hour off or twice; later occurrences follow the new rules. A schedule with a calendar, an availability calendar, or a bound skips such an occurrence as `source-changed` instead ([scheduling](./scheduling.md#catch-up-misfire-and-expiration)).
- **On SQL Server with READ_COMMITTED_SNAPSHOT on, a child enqueued in a transaction still open when its parent is cancelled escapes the cancel.** The cancel does not wait for the uncommitted child, which runs once the transaction commits; with the option off the cancel waits for that transaction. Cancelling the parent again walks it. On PostgreSQL the cancel waits for that transaction, and only a child enqueued in a REPEATABLE READ or SERIALIZABLE transaction that began before the cancel escapes it.
- **A worker that crashes on every replay of a timed-out wait loops without spending budget.** The job re-alerts on every reminder, and an operator cancel ends it.
- **`OnTerminal` and `Info` never alert on a permanent job's ordinary failures**, which return it to `Idle` instead of landing Failed. Use `OnFailure` for recurring work.
- **A permanent job whose firing fails for good keeps its automatic alert open at `Failures`** after its next healthy firing, restarted or not: admission resets the failure count that marks a success as a recovery. A firing that recovers on a retry within its budget does resolve. Otherwise resolve the alert by hand, or run the definition at `Audit`, where every success is recorded. One-shot jobs auto-resolve at every level.
- **The alert projector assumes the database clock never steps backwards.** After a step, failures stamped below the cursor are missed. Repair by setting the `alerts-cursor` variable of the namespace's `sys.alerts` job in `checkpoints` to `{"Ticks":<UTC ticks of an instant before the step>,"EventId":0}`, no further back than `AlertRetention`.
- **A writer that freezes past twice the command timeout** can commit an event the projector already passed, which is then missed (never wrong). Keep command timeouts the same across the fleet.
- **A requeue command can requeue a row a second time.** A relay pass that stalls between planning the command and requeueing its rows, while another pass finishes it and the row is quarantined again, requeues that row once more and records no event for it. The row retries as the operator asked; nothing else changes.

## Ordering

- **Claims are ordered, work is not.** There is no global FIFO: a claim takes the highest priority and then the earliest due instant, so a retry waiting out its backoff is overtaken by whatever is due sooner. A lane is the exception on purpose: its jobs run one at a time in enqueue order, and a failing head holds the lane through its retries ([Lanes](./developer-guide.md#lanes)).
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
- **Retiring a definition cancels its parked jobs but not their descendants.**
- **A recurring firing still running when its definition is retired is cancelled** unless it succeeds or spends its failure budget, which return the permanent job to `Idle` as usual. A cancelled permanent job stays cancelled after a deploy reactivates the definition, so restart it. While the definition stays retired, retention removes the job once its retention runs out, and the next deploy that declares the definition writes a fresh permanent job.
- **A worker learns of a retire on its next heartbeat**, so an attempt that starts in that gap still runs once. A job its handler paused stays Paused until it is resumed or cancelled.

## Dashboard and API exposure

Authentication is the host's: Acta ships none. The surface is local-only by default with controls off, and behind a reverse proxy on the same host `LocalOnly` protects nothing.

- **The dashboard hides only the controls Acta's own permission policies refuse.** `permissions` on the capabilities read reflects the four `RequirePermissions` policies, evaluated for that read, and lists all four without them, so a control a host refuses through its own authorization (`ConfigureEndpoints`, a rule that reads the route) still shows and answers 403.
- **Behind a host that authorizes the dashboard's mount with its own cookie scheme, a lapsed session shows errors until the page is reloaded.** Such a host answers the dashboard's calls with a redirect to its sign-in page, not the 401 that `RequirePermissions` answers and the dashboard stops on, and only a reloaded page follows the redirect.

## Storage and providers

SQLite has concurrency limits compared with server databases. Use SQL Server or PostgreSQL for
distributed multi-worker deployments.

On SQLite, Acta's tables share `main` with the application's: the schema must be `main`, so there is no
`acta` schema to keep them apart. The provisioning script creates each table only if it is missing, so an
application table that already holds one of Acta's names (`migrations`, `jobs`, `events`, `tags`,
`tenants`, ...) stays in place, and what follows depends on its columns. A `tenants` table without Acta's
columns stops provisioning at the first index on them. A `tags` table without them lets provisioning
succeed, and Acta's first tag write fails. An ORM's `migrations` table, such as TypeORM's, fails the
script, and once it holds rows the refusal claims a newer Acta release upgraded the database. A table whose
columns happen to match is shared with no error at all. Keep Acta's table names free in that file. A separate
database file for Acta avoids the clash, but then an enqueue can no longer share a transaction with the
application's writes.

On SQLite, a `JobId` returned by a transactional enqueue that then rolls back is handed out again.
`sqlite_sequence`, which backs `AUTOINCREMENT`, is an ordinary table, so the counter is rolled back with
the transaction that advanced it and the next insert receives the same id. PostgreSQL and SQL Server
allocate from non-transactional sequences, which burn the value instead. Acta's own contract is the same
on all three: a transactional enqueue that rolls back leaves no durable row. What differs is what the
returned identity means afterwards: an application that holds `EnqueueJobResponse.JobId` across a
rollback and looks it up later can, on SQLite, read a *different* job that has since been given that id.
Hold `JobRef` instead. It is a UUIDv7 minted in your process before the row is written, so you have it
in advance and nothing else will ever carry it, whereas a `JobId` is only meaningful for an enqueue that
committed.

A namespace id is 32 bits, so one database can allocate around 2.1 billion namespaces over its
whole lifetime. Ids are never reclaimed (deleting a namespace does not return its id, so the
ceiling counts every namespace ever created rather than the number that currently exist), but at
this width that is an accounting note, not a budget: a deploy allocates an id only for a
genuinely new namespace name (a repeated deploy of an existing namespace allocates
nothing), and namespaces are created deliberately, one per bounded context, not per request.
SQLite's `namespaces.namespace_id` is a 64-bit `AUTOINCREMENT`; 2,147,483,647 is the portable limit.

Microsoft.Data.Sqlite (verified in 10.0.15, the version Acta pins) carries an unsynchronized enumeration
in `SqliteConnection.Deactivate`: returning a pooled connection walks the custom-function dictionary
with no lock, so a concurrent reclaim can fault inside the driver. A plain user never sees it (the
dictionary is empty unless `CreateFunction` was called), but Acta registers its `acta_blob` and
`acta_error` functions on every open, which makes the upstream race reachable. Acta makes it
reachable; it does not cause it. Observed only as a rare test-suite fault under heavy cross-process
parallelism, never reproduced in isolation, and 1.0 deliberately changes nothing for it: the
fault sits in the driver's pool, and `Pooling=false` would trade it for a fresh native open per
connection, a real cost that would need benchmarking first.

Acta requires no ambient isolation level: every mutation is guarded by explicit lock hints or a
compare-and-swap predicate, so job correctness does not depend on snapshot isolation. On SQL Server it
runs and is tested with READ_COMMITTED_SNAPSHOT on and off: no Acta statement waits on an
application's open transaction, except the write-write waits RCSI has too (the same lane, parent
row, or deduplication key), a lookup of the very job that transaction is still enqueuing, and a
statement of that transaction that locks a whole page or table, which no read can skip. Without RCSI
two things differ: a tree cancel waits for a transaction that is enqueuing a child into the tree, and
Explain is not one snapshot. In both modes a dashboard page or count can briefly miss a row another
transaction is changing, and a retire can leave a job another transaction holds at that instant
unfinished under the retired definition. Acta turns READ_COMMITTED_SNAPSHOT on only in a database its
development flag has just created, and leaves the setting as it is everywhere else.
PostgreSQL runs plain Read Committed and SQLite WAL journal
mode; reverting SQLite to a rollback journal reintroduces a global write lock. An application-imposed Serializable level makes the signal-wait arming race surface as a
serialization retry rather than resolving in place: safe, but untested. Only the preferred
configuration is exercised by the conformance suites.

On SQL Server, sixteen worker processes enqueuing and claiming at about 4,000 jobs a second see short
stalls, up to about 0.4 s, in which enqueues wait, mostly on SQL Server's throttle for inserts into the
newest page of `jobs` and `jobs_state`. Throughput holds, but the stalls lift an enqueue's p99 to between
0.5 s and 1.9 s in a run. The benchmark reports of rc.9, rc.10, and rc.11 name the runs.

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
