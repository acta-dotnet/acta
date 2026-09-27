# SQL execution policy

PostgreSQL and SQL Server execute Acta ledger mutations through atomic routines; reads use embedded
SQL and installed views; SQLite implements the same store contracts with transactional inline SQL.

This is the execution policy referenced by [the design decisions](design.md). The server
providers each install 59 routines: the 54 mutation operations plus `UpdateAlertDelivery`,
`ResolveJobAlerts`, `RepairRecoverySlot`, `AcquireSlot`, and `ReserveRate`. Hot paths, control operations, catalog registration,
recovery, and maintenance all follow the same rule. Routine placement is a compatibility decision,
not an inference from SQL size.

## Resources and execution

- `Operation.routine.sql` defines an installed atomic ledger operation on PostgreSQL or SQL Server.
- `Operation.sql` is executable SQL shipped with the binary. Ledger reads use this form on every
  provider; SQLite also uses it for mutations.
- `Name.view.sql` defines an installed read model. Views remain database objects, queryable with SELECT.

`SqlResourceCatalog` resolves exactly one implementation for each operation/provider. Missing or
ambiguous resources fail before a connection opens. `DbSession` uses the resource kind to configure
the command. There is no provider-wide routine switch. `SupportsBatchCompletion` describes only
native batch completion, which SQLite does not implement.

Shared stores use query methods for reads and execute methods for mutations. Stores never decide
transaction ownership from the number of SQL statements. Parameter binding stays provider-specific,
including PostgreSQL arrays and SQL Server TVPs.

**Every semantic mutation store call is atomic.** The database changes and their audit evidence
commit or roll back together. A higher-level API may orchestrate several store calls; this rule does
not make those separate calls one transaction.

## Transaction and retry ownership

| Execution path | Atomicity and ownership |
| --- | --- |
| Owned PostgreSQL ledger call | One function invocation, atomic in the database statement transaction. |
| Owned SQL Server ledger call | One procedure invocation; the procedure begins and commits its transaction inside the call. |
| Owned SQLite mutation | `DbSession` starts an immediate transaction, executes and reads the file, then commits. |
| Caller-transaction enqueue | All three providers join the supplied transaction. Acta never commits, disposes, or retries it. |
| Ordinary read | No additional write transaction. |

SQL Server procedures use `SET XACT_ABORT ON` and `TRY/CATCH`. They record `@@TRANCOUNT` at entry,
begin and commit only if they own the transaction, and roll back an owned active transaction before
rethrowing errors. An outer transaction remains the owner's responsibility. SQL Server does not
provide independent nested commits: a database error can invalidate or abort the caller's transaction.

Normal server mutations require one operation command, with no separate C# begin/commit calls.
Do not equate ADO.NET method counts with measured network round trips; driver batching and protocol
behavior matter. SQLite's immediate transaction is an in-process database operation.

The session consumes the intended business result and drains the reader, including trailing errors,
before reporting success. A C# mapping failure cannot promise rollback of an already committed server
operation. SQLite can roll back mapping failures while its owned transaction remains uncommitted.

Owned execution retries only confirmed aborted conflicts: PostgreSQL deadlock `40P01`, SQL Server
deadlock victim `1205`, and SQLite busy/locked conflicts after cleanup. Failed attempts release their
resources before another attempt opens. Binding, mapping, and disposal failures are not database-abort
evidence and never trigger replay. Connection failures or uncertain commit outcomes are not automatically
replayed. Session-owned commit and final teardown remain outside the retry region.

## Explicit exceptions and maintenance

SQL Server's `locks` table must not use `HOLDLOCK` or serializable gap probes. Acquire uses a
conditional point update followed by an insert on a miss; the primary key arbitrates competing
inserts. A duplicate-key loser rolls back its owned transaction and returns not-acquired. Errors
inside a supplied transaction propagate so its owner can roll it back. Concurrency tests hold the
insert stage open to verify that unrelated missing keys can reach it together and same-key races
return exactly one token.

External-outbox source commands operate on a producer-owned database, so they remain embedded SQL;
Acta installs no ledger routines there. PostgreSQL executes the claim batch in one implicit transaction;
SQL Server's multi-statement claim includes an ownership-aware transaction block; SQLite uses an
immediate transaction. Source reads use the query path. Schema bootstrap and object installation have
their separate migration transaction and lock.

`PurgeExpiredData` remains one routine identity, but each invocation purges one selected section's
bounded batch. `RetentionCoordinator` reads database time once, calculates fixed sweep cutoffs, and
visits jobs, events, settled alerts, undelivered alerts, poison-skip checkpoints, workers, expired locks,
and unreferenced lanes in that order. Defaults remain 1,000 rows and 50 iterations per section. An empty batch ends its
section. Each batch is atomic; failure or cancellation later in the sweep preserves completed batches.
The coordinator never retries the whole sweep. Committed undelivered-alert deletions produce one
warning per sweep, including when a later batch fails or cancellation stops the sweep.

Every SQL Server statement that updates `runtimes` through a join carries `WITH (FORCESEEK)`. The
driving set is always a CTE or table variable, which the optimizer estimates at low cardinality;
without the hint it may source the update from a clustered scan of `runtimes`, take an update lock per
row, escalate to a table lock once the backlog passes the escalation threshold, and hold it to commit,
so every concurrent insert waits out the whole statement. `pk_runtimes` is keyed on `job_id` and every
such join predicate resolves through it, so a seek is always available and the hint cannot fail to
compile. The claim batch, the single claim, batch completion, and the stuck-job reclaim all take it.
`StartExecution` does not: it filters on a point predicate and always seeks.

Statements that touch many `runtimes` rows lock the base row first and never hold an index key while
waiting for one. `StartExecution` and `CompleteExecution` change `status_code` and
`leased_by_worker_id`, both key columns of `ix_runtimes_worker_inflight`, so they lock the clustered
row and then move the index key; a worker heartbeat that walked that index instead would hold the key
and wait for the row, which is the inversion that deadlocks. `ExtendWorkerLeases` therefore reads the
in-flight ids first and updates through a primary-key seek.

`ExtendWorkerLeases` never waits for a row: it skips one another transaction holds (`READPAST` on SQL
Server, `SKIP LOCKED` on PostgreSQL) and returns every in-flight id it read with a flag saying whether
it was renewed. A child's completion locks its own `runtimes` row and then its parent's, the reverse of
`job_id` order, so a renewal that waited on either would deadlock with it when one worker runs both.
The caller cancels a running attempt only when its id is missing, and moves the attempt's lease
deadline only when the row was renewed; a skipped row is renewed on the next beat, well inside the
lease window. On PostgreSQL batch completion locks its rows in `job_id` order, and the buffered flush
is sorted by job id before its ordinals are assigned.

Routines that lock both `checkpoints` and `runtimes` take the checkpoint slot first; `raise_signal`
and `complete_execution` both do. `arm_or_consume_sleep_timer` is the one exception, taking the
`runtimes` row as a job-level mutex before reading its slot, so a timer arming concurrently with a
signal raise on the same job acquires the two in opposite orders. No deadlock graph has ever shown
that pair, and it is not reachable while both hold row locks on different slots, so the mutex stays
until a run demonstrates otherwise; removing it would drop the serialization that two different timer
names on one job rely on.

### Foreign keys in set-based writes

SQL Server validates a foreign key inside the inserting statement's own plan, with locking reads even
under read-committed snapshot, and chooses the join by the insert's estimated row count. A batch
insert estimated at hundreds of rows validates through a merge join over an ordered scan of the whole
parent table, which takes shared page locks; concurrent producers whose uncommitted rows share those
parent pages then wait on each other and deadlock. The driving set is table variables, which the
optimizer sizes at their real count whenever the statement compiles after they fill: at a deferred
first compile, or at a recompile after a statistic the plan read goes stale. An unhinted batch write
can therefore switch to that scan at any time. A pinned seek reads more pages than a scan of a small
child table, but it locks only the batch's own keys.

- Both `runtimes` inserts in `enqueue_batch` carry `OPTION (LOOP JOIN)`, so `fk_runtimes_jobs` and
  `fk_runtimes_lanes` seek each parent by key and lock only rows the batch inserted or already holds.
- The `results` insert in `complete_executions_batch` carries it for `fk_results_jobs`. Flushers hold no
  lock on `jobs`, so an unhinted scan there waits behind an uncommitted enqueue rather than deadlocking,
  but it still reads the whole table per batch.
- The same rule covers set-based deletes and locking reads driven by a table variable.
  `purge_expired_data` names every permanent table it deletes from or reads under `UPDLOCK` through
  `@del` or `@lock_del` with `WITH (FORCESEEK)`, so the table is always the inner side, sought by key.
  `OPTION (LOOP JOIN)` alone is not enough there. Compiled at one row, it keeps nested loops but makes
  a small permanent table the outer input and scans all of it under update locks. The `jobs` delete
  keeps `LOOP JOIN` as well, for its cascades into five child tables.
- The expired-lock sweep is global, so every namespace's retention pass meets the others there.
  Unhinted, its delete scanned all of `ix_locks_reclaim_expired` under update locks. Two sweeps then
  deadlocked on each other's staged rows, and a sweep stalled or rolled back that way left expired rows
  that concurrent sweeps had skipped under `READPAST`. The sweep also held its staged rows' index keys
  while it waited for their clustered keys, the reverse of every lock writer, and deadlocked against
  `reserve_rate` moving a bucket's expiry. It now stages without holding locks. Its delete locks each
  row by clustered key with `READPAST`, skipping one a writer holds, and re-checks the expiry.
- An unlaned batch runs the plain inserts: no identity ordering, no lane probe. A laned batch orders
  its `jobs` insert by ordinal and marks each idle lane's head before the `runtimes` insert, reading
  `ix_runtimes_lane` once per lane through `WITH (FORCESEEK)`, so that insert reads table variables
  alone.
- `register_scheduled_jobs` stays unhinted. It runs under one exclusive application lock, so two calls
  never race, and its set is one manifest's recurring definitions, which stays small.
- Row-at-a-time writes, updates that leave foreign-key columns alone, and writes to `tags`, `events`,
  and `alerts`, which carry no foreign key, validate nothing.
- PostgreSQL checks each foreign key with a per-row key lookup in a trigger and needs no hint. SQLite
  runs one writer at a time.

### Alert lock order

A routine that writes a job's alerts locks the job row first, then the alert rows. `purge_job` holds the
job row while it deletes the job's alerts, and `raise_job_alert` and `resolve_job_alerts` lock the same
row before they touch an alert: `UPDLOCK` on SQL Server, `FOR KEY SHARE` on PostgreSQL against the
purge's `FOR UPDATE`. A raise or resolve that meets a purge of its job waits for it. A raise that then
finds the job gone is refused, so no alert outlives its job.

- `ix_alerts_job` (`job_id, id`, filtered to rows with a job) holds a job's alerts. On SQL Server,
  purge and resolve seek it without locks, which the held job row makes safe, and then lock each row by
  primary key. On PostgreSQL and SQLite, their equality on `job_id` seeks it directly. Before that index,
  each SQL Server path scanned `pk_alerts` under update locks, and scans for different jobs deadlocked.
- On SQL Server a row that has a deduplication identity is locked through its
  `ix_alerts_dedupe_identity` key before the row itself. `raise_job_alert` seeks that index under
  `UPDLOCK, HOLDLOCK`, pinned by name, and range-locks the identity and its next key. `purge_job` and
  the alert retention sections lock identity rows through the same index before deleting them.
  Retention stages its batch without locks and locks with `READPAST`, so it skips a row a raise holds.
  A delete that took the row first and the index key second deadlocked against a raise, whichever job
  that raise was for.
- Acknowledge and manual resolve lock one alert row by its ref and only read the job, and delivery
  updates are a single-row compare-and-swap by id. None of them waits on a job row while holding an
  alert, so they need no job lock.
- SQLite runs one writer at a time, and each alert write reads its job inside that one transaction.

### Lane lock order

Every routine that touches a laned job locks the `lanes` rows it needs first, in ascending `lanes.id`,
and only then locks `jobs`, `checkpoints`, or `runtimes` rows, CAS updates included. `runtimes.lane_id`
never changes after insert, so a routine may read it unlocked to learn which lane to lock. The lane
lock is what serializes a lane: enqueue decides Ready or Blocked under it, every settle to Succeeded,
Failed, or Cancelled promotes the next member under it, and retention deletes a lane that no runtime
row references under it, skipping a lane another transaction holds and re-checking the reference under
the lock.

- Enqueue resolves the effective lane (the request's, else the definition's) and locks it before the
  parent row, so a parent enqueuing a child into a lane cannot hold the parent while a completing
  sibling in that lane holds the lane and waits for the parent.
- An enqueue batch first inserts the missing lane names, sorted by name, without locking existing
  rows, then locks the whole set in id order. A lane inserted by an uncommitted transaction is
  invisible to every other transaction, so no settle can contend for it. Completion batches lock
  their distinct lanes in id order before any runtime row.
- The upsert retries when retention deleted the lane between the lookup and the lock: the lock finds
  no row, and the next pass inserts the lane again.
- On SQL Server every lane lock is an `UPDLOCK` seek by `id` on the clustered key. A lookup by name
  resolves the id first without a lock, because `ux_lanes_namespace_name` covers `id`, so a lock taken
  through it would land on that index's key alone and never meet a lock taken by id.
- A lane runs at most one member at a time: Ready, Suspended, Dispatched, and Executing are the running
  statuses. A restarted member is older than the members already waiting, so it can wait Blocked below
  the running one.
- Promotion re-reads under the lock: it takes the lowest-id unfinished member of the lane, promotes it
  when it is Blocked and no member runs, and otherwise leaves it as the head. A settle ends the one
  running member, so its promotion never meets another. Pausing the running member, by the pause verb
  or by its own handler, promotes the same way: a Paused head holds its lane only against younger
  members, so an older restarted member waiting Blocked below it runs next. Promotion never updates
  through a subselect that a concurrent change could turn into a zero-row update.
- Operator verbs on a laned job (cancel, pause, resume, reschedule, reprioritize, restart, retire)
  lock the lane before the job's row, Blocked followers included. Resume, reschedule, and restart
  choose Blocked when another member runs, or when an older member is unfinished and the job itself
  was not running, and Ready otherwise.
- A definition retire locks the lanes of the definition's parked laned jobs in id order, cancels
  every parked job of the definition, and only then promotes each of those lanes once, so no member of
  the retired definition is promoted on the way. All of it is one transaction.
- `restart_job` reactivates a finished laned job in place, under the lane lock. It refuses the job
  while an unfinished ancestor or descendant sits in the same lane: the restarted job would run ahead
  of a descendant its handler may wait for, the cycle the enqueue ancestor guard refuses. Both walks
  run under the lane lock, which every enqueue into that lane also takes.
- `reclaim_stuck_jobs` also repairs stranded lanes, whose lowest-id unfinished member is Blocked while
  no member runs.
  Each pass visits at most 1,000 of its namespace's lanes, starting at a random lane id and wrapping,
  seeks each one's head through `ix_runtimes_lane`, and repairs at most 100 of the stranded lanes it
  finds, in visiting order, so the cost is bounded and stateless and every lane is visited
  eventually. Repair is a safety net for hand edits, so the cap trades a large backlog's drain time
  for a pass that stays short. The walk reads without locks. The pass then locks the stranded lanes
  and the stuck rows' lanes in one id-ordered pass that skips a lane another transaction holds. On SQL
  Server it re-reads the heads under those locks and promotes them in one statement, then writes the
  `job.lane-repaired` events in one more.
- A caller transaction that combines several operations can still take a lane after a row it already
  holds. The database breaks that deadlock, and the aborted owned call is retried like any `40P01` or
  `1205` victim.

## Provisioning, compatibility, and SQL access

Tables, columns, indexes, constraints, and durable types belong in numbered `MNNN` migrations.
Routine and view definitions are versionless objects: `SqlObjectInstaller` reapplies current bodies
after checking pending migrations, under the same migration lock, including when no migration is
pending. A body edit does **not inherently require a numbered migration**. Embedded query changes
require neither migration nor installed-object replacement.

A routine whose signature changes carries its own cleanup, because `CREATE OR REPLACE` neither
replaces across arities nor changes a return type. A retired arity is dropped after the create; a
changed return type must be dropped before it, and that leading drop names no argument list, because
the routine-body parameter gate reads the file's first parenthesis as the parameter list. The
routine layer therefore reinstalls onto a database from the previous release. Reprovisioning is
required only where a table definition moved, which last happened before 1.0 for the SQL Server
Unicode actor key and cannot happen again under the frozen baseline.
From 1.0 onward, an installed routine remains a routine. An inline operation may be promoted
deliberately; routine-to-inline demotion is outside the 1.x policy.

During a rolling deployment all workers share the installed routine body. N+1 provisioning changes
the implementation that N workers invoke; N provisioning during rollback may reinstall N's body.
Changes must therefore preserve the supported parameter, result, and semantic contracts in both
deployment directions. Compatible table DDL alone is insufficient. Body replacement avoiding MNNN
does not remove this executable compatibility obligation.

Dashboard/API are the normal administration surface; human database access can be restricted without
making Acta opaque. Ordinary SELECT inspection and the documented direct SQL enqueue recipes remain
supported, including SSMS, DBeaver, and psql. SQL enqueue becomes visible to worker polling after commit;
it does not itself send an application transport wake-up. See [SQL recipes](../guide/sql-recipes.md).
This policy does not declare every internal routine an independently supported operator API or require
an EXECUTE-only runtime account. Installed views remain the curated query surface; storage tables and
custom payload encodings retain their existing compatibility and decoding limits.
