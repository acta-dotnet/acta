# SQL execution policy

PostgreSQL and SQL Server execute the hot ledger mutations through installed atomic routines and
everything else through embedded SQL and installed views; SQLite runs every mutation as embedded SQL
inside a transaction the session owns.

This is the execution policy referenced by [the design decisions](design.md). The server providers
each install fourteen routines: enqueue, claim, complete (one batch call for every completion shape), the step
writes, checkpoints, leases, meters, job signals and sleep timers, recovery, and retention. Every other
operation, registration, alerts, locks and slots, the outbox inbox, and control verbs included, is embedded SQL
on every provider.
`SqlCommandCatalogTests` pins the routine list; moving an operation across that line is a decision,
because an installed routine is a compatibility obligation and every edit to one is a migration.

## Resources and execution

- Each operation has one file per dialect, addressed by its stem: `Operation.pg.sql` for inline SQL shipped with the binary, or `Operation.routine.pg.sql` for an installed atomic ledger operation on PostgreSQL or SQL Server, whose body reaches a database only inside a migration.
- A command with no embedded file for the dialect runs as the routine of that name; one with a file runs as its text. Ledger reads are inline on every provider; SQLite is inline for mutations too.
- A file whose stem ends in `View`, under `Schema/Views/`, is an operator read model, installed by a migration like a routine. Views are operator convenience: no code and no test reads one.

`SqlResourceCatalog` resolves at most one file for each stem and dialect. Two files with one stem fail when the catalog is built; a stem with no file is a routine, and `SqlCommandCatalogTests` pins which stems those are, so a typo cannot pass as one. `DbSession` uses that kind to configure the command. There is no provider-wide routine switch.

A handler reads through the session's query methods and mutates through its execute methods, and an
inline mutation on PostgreSQL or SQL Server runs inside a transaction the session opens and commits
around the one command, so it is as atomic as a routine. A handler never decides transaction
ownership from the number of SQL statements. Parameter binding stays provider-specific, including
PostgreSQL arrays and SQL Server TVPs.

**Every mutation operation is atomic.** The database changes and their audit evidence commit or roll
back together. A higher-level API may orchestrate several operations; this rule does not make those
separate calls one transaction.

## Transaction and retry ownership

| Execution path | Atomicity and ownership |
| --- | --- |
| Owned PostgreSQL ledger call | One function invocation, atomic in the database statement transaction. |
| Owned SQL Server ledger call | One procedure invocation; the procedure begins and commits its transaction inside the call. |
| Owned inline mutation | `DbSession` starts a transaction (immediate on SQLite), executes and reads the file, then commits. |
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
deadlock victim `1205` and `2801` (an installed routine changed under a concurrent bootstrap), and
SQLite busy/locked conflicts after cleanup. Failed attempts release their
resources before another attempt opens. Binding, mapping, and disposal failures are not database-abort
evidence and never trigger replay. Connection failures or uncertain commit outcomes are not automatically
replayed. Session-owned commit and final teardown remain outside the retry region.

## Explicit exceptions and maintenance

Acquiring a lock or a concurrency slot in SQL Server's `locks` table must not use `HOLDLOCK` or
serializable gap probes. Acquire uses a conditional point update followed by an insert on a miss; the primary key arbitrates competing
inserts. A duplicate-key loser rolls back its owned transaction and returns not-acquired. Errors
inside a supplied transaction propagate so its owner can roll it back. Concurrency tests hold the
insert stage open to verify that unrelated missing keys can reach it together and same-key races
return exactly one token. `reserve_rate` is the deliberate exception: it probes the meter row with
`UPDLOCK, HOLDLOCK` before the row exists and holds it to commit, so the expiry sweep's `READPAST`
skips the row rather than racing it, and the turn the call books is persisted before it admits
anything.

External-outbox source commands operate on a producer-owned database, so they remain embedded SQL;
Acta installs no ledger routines there. PostgreSQL executes the claim batch in one implicit transaction;
SQL Server's multi-statement claim includes an ownership-aware transaction block; SQLite uses an
immediate transaction. Source reads use the query path. Schema bootstrap and object installation have
their separate migration transaction and lock.

`PurgeExpiredData` remains one routine identity, but each invocation purges one selected section's
bounded batch. The events and jobs sections honor the alert floor: an alert-required event above
the alerts projector's consumed cursor, and the job that owns it, are excluded until projected.
`RetentionJob` reads database time once, calculates fixed sweep cutoffs, and
visits jobs, events, settled alerts, undelivered alerts, poison-skip checkpoints, workers, expired locks,
and unreferenced lanes in that order. Defaults remain 1,000 rows and 50 iterations per section, and an
empty batch ends its section. The jobs section is the exception: no index finds expired jobs, so each
call walks `jobs_state` down by job id from the cursor the previous tick stored on the slot
(`p_before_job_id`, the `purge-cursor` job variable), scans ten batches' worth of rows, deletes the
namespace's expired jobs among them, and answers where it stopped; a tick takes up to 200 such slices,
two million rows, or stops early at the oldest row or once a delete budget's worth of jobs is gone.
Each batch is atomic; failure or cancellation later in the sweep preserves completed batches.
`RetentionJob` never retries the whole sweep. Committed undelivered-alert deletions produce one
warning per sweep, including when a later batch fails or cancellation stops the sweep.

Every SQL Server statement that updates `jobs_state` through a join carries `WITH (FORCESEEK)`, except
`register_schedules` (see the foreign-key section below). The
driving set is always a CTE or table variable, which the optimizer estimates at low cardinality;
without the hint it may source the update from a clustered scan of `jobs_state`, take an update lock per
row, escalate to a table lock once the backlog passes the escalation threshold, and hold it to commit,
so every concurrent insert waits out the whole statement. `pk_jobs_state` is keyed on `job_id` and every
such join predicate resolves through it, so a seek is always available and the hint cannot fail to
compile. The claim batch, the single claim, batch completion, and the stuck-job reclaim all take it.

Statements that touch many `jobs_state` rows lock the base row first and never hold an index key while
waiting for one. `CompleteExecutions` changes `job_status_code` and
`leased_by_worker_id`, both key columns of `ix_jobs_state_worker_inflight`, so it locks the clustered
row and then moves the index key; a worker heartbeat that walked that index instead would hold the key
and wait for the row, which is the inversion that deadlocks. `ExtendWorkerLeases` therefore reads the
in-flight ids first and updates through a primary-key seek.

`ExtendWorkerLeases` takes the (job, execution) pairs and the lock holds the process holds, and
never waits for a row: it skips one another transaction holds (`READPAST` on SQL Server, `SKIP
LOCKED` on PostgreSQL) and answers each pair it still finds leased by the worker at that execution
with a flag saying whether it was renewed. A child's completion locks its own `jobs_state` row and then
its parent's, the reverse of `job_id` order, so a renewal that waited on either would deadlock with it
when one worker runs both. The caller cancels an attempt only when its pair is not answered, and
moves its lease deadline only when the row was renewed; a skipped row is renewed on the next beat,
well inside the lease window. The lock holds renew first and without skipping, then the `jobs_state` rows with
skipping, so the routine never waits on a `jobs_state` row while it holds a `locks` row. The answer also carries the namespace's
newest `definitions.modified_at_utc`, on which the worker re-reads its policy.

Statements that lock both `checkpoints` and `jobs_state` take the checkpoint slot first. `raise_signal` does,
and `complete_executions` takes the slots its rows await before their `jobs_state` rows; a child that
lands then takes its parent's latch before the parent's `jobs_state` row, both after its own. A batch
holding a parent and one of its grandchildren can therefore deadlock with a batch landing the child
between them; the victim's write repeats. `ResetJobState` takes the `jobs_state` row and then deletes the
job's checkpoints, and `PurgeJob` deletes them before the job row; both run on a
job no worker is advancing on another slot at that moment. `arm_or_consume_sleep_timer` is the other
exception, taking the
`jobs_state` row as a job-level mutex before reading its slot, so a timer arming concurrently with a
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

- Both `jobs_state` inserts in `enqueue_jobs` carry `OPTION (LOOP JOIN)`, so `fk_jobs_state_lanes` seeks
  each lane by key and locks only rows the batch inserted or already holds.
- The same rule covers set-based deletes and locking reads driven by a table variable.
  `purge_expired_data` names every permanent table it deletes from or reads under `UPDLOCK` through
  `@del` or `@lock_del` with `WITH (FORCESEEK)`, so the table is always the inner side, sought by key.
  `OPTION (LOOP JOIN)` alone is not enough there. Compiled at one row, it keeps nested loops but makes
  a small permanent table the outer input and scans all of it under update locks.
- The expired-lock sweep is global, so every namespace's retention pass meets the others there.
  Unhinted, its delete scanned all of `ix_locks_reclaim_expired` under update locks. Two sweeps then
  deadlocked on each other's staged rows, and a sweep stalled or rolled back that way left expired rows
  that concurrent sweeps had skipped under `READPAST`. The sweep also held its staged rows' index keys
  while it waited for their clustered keys, the reverse of every lock writer, and deadlocked against
  `reserve_rate` moving a bucket's expiry. It now stages without holding locks. Its delete locks each
  row by clustered key with `READPAST`, skipping one a writer holds, and re-checks the expiry.
- An unlaned batch runs the plain inserts: no identity ordering, no lane probe. A laned batch orders
  its `jobs` insert by ordinal and marks each idle lane's head before the `jobs_state` insert, reading
  `ix_jobs_state_lane` once per lane through `WITH (FORCESEEK)`, so that insert reads table variables
  alone.
- Every lookup of a lane's unfinished members, its head or whether any of them runs, carries `WITH (INDEX
  (ix_jobs_state_lane), FORCESEEK)`. The lane id is a variable and the lookup wants one row, so unhinted the
  optimizer can walk `pk_jobs_state` from the oldest job until it meets the lane, a cost that grows with
  every job the table retains. Each lookup names `lane_id IS NOT NULL` and a status list inside the
  index filter, so the hint always compiles.
- `register_schedules` stays unhinted outside its schedule writes. It runs under its namespace's exclusive schedule lock
  ("Schedule lock"), so two calls in one namespace never race, and its set is one manifest's recurring definitions, which
  stays small. Its updates of `schedules` and `schedules_state` seek by key ("Locking reads seek"), and the heal seeks
  the namespace's cursors through `ix_schedules_state_namespace_next` by name.
- Row-at-a-time writes, updates that leave foreign-key columns alone, and writes to `tags`, `events`,
  and `alerts`, which carry no foreign key, validate nothing.
- PostgreSQL checks each foreign key with a per-row key lookup in a trigger and needs no hint. SQLite
  runs one writer at a time.

### Locking reads seek

On SQL Server a read under `UPDLOCK`, `HOLDLOCK`, or `XLOCK` takes a lock on every row it reads, so a scan
under those hints locks the table a row at a time and escalates. Statistics compiled while a table was
small price a scan below a seek per row, and a retained table only grows, so every locking read that
could scan a permanent table carries `FORCESEEK` (a primary-key equality already seeks), naming the index when the predicate restates a filtered index's
filter so the hint always compiles:

- The deduplication probes in `enqueue_job` and `enqueue_jobs` seek `ux_jobs_deduplication_key_root`
  or `ux_jobs_deduplication_key_child`; the parent lookup seeks `pk_jobs`. In `enqueue_jobs` the probe
  is a join's inner side and names both seek columns: with the index alone the optimizer may seek the
  leading column and test the key as a residual, reading every key under it once per batch row.
- The retention purge's staging read seeks `jobs_state` from its cursor and `jobs` by key, and its event
  read seeks `ix_events_namespace_created`.
- The outbox claim and its lease recovery seek without naming an index, because the producer owns that
  table's DDL.
- A completion's and a registration's updates of `schedules` and `schedules_state` seek by key. Fires in one namespace
  share its schedule lock and registrations in different namespaces run at once, so an update that scanned would wait
  on rows another holds, and two such scans deadlock on each other.

The claim and the completion read `jobs` and `jobs_state` without locks, through joins from a table
variable, and pin those joins with `FORCESEEK` too: the same stale estimate turns them into a scan whose
cost grows with every job the table retains.

On PostgreSQL the predicate does the pinning. `complete_executions` locks its batch's `jobs_state` rows,
and `extend_worker_leases` reads and locks the held ones, by `pk_jobs_state` probes, with the worker test
written `leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id`. Written `=`, the test implies
the filter of the partial `ix_jobs_state_worker_inflight`, and a cached generic plan may walk the worker's
whole range of that index instead. That range holds a dead entry for every attempt the worker finished
until vacuum removes it, so the walk grows between vacuums, and which plan runs depends on the statistics
autoanalyze last took. No index serves `IS NOT DISTINCT FROM` and it implies no `IS NOT NULL`, so the
primary key is the only index the planner can use. The worker id is never NULL and
`ck_jobs_state_inflight_leased` gives every Executing row a worker, so the rows matched are the same.

### Deduplication lock

On SQL Server an enqueue serializes on its deduplication key through an application lock, not a range
lock. `enqueue_job` and `enqueue_jobs` take `sp_getapplock`, exclusive and owned by the transaction, on
`<schema>.dedup:n<namespace id>:<key>` for a root job or `<schema>.dedup:p<parent id>:<key>` for a
child, and only then probe the deduplication index under `UPDLOCK`. An application lock binds only
the code that asks for it, so every statement that inserts a keyed job takes it: the two enqueue
routines and `RegisterSchedules`, which inserts the recurring slots under their names.

- A probe under `HOLDLOCK` for a key not yet stored locks the gap the key falls in. A key newer than every
  stored key shares the last gap with every other new key, so enqueues of different keys in a namespace
  waited for each other's commit, a caller's whole transaction included. The application lock waits
  only for the same key, which is what PostgreSQL's `INSERT ... ON CONFLICT` waits for.
- The lock is taken after the batch's lanes and before any job row. A batch takes its keys in name
  order, so two batches sharing keys never deadlock on them.
- `sp_getapplock` reports a deadlock by return code rather than error 1205, so each of them raises
  51205 on any refusal, and the dialect retries it as it retries 1205.

### Slot locks

A job's steps and checkpoints are the same case on SQL Server: running jobs are the newest, so each
job's first step or checkpoint falls in the last gap of `pk_steps` or `pk_checkpoints`, and a
probe under `HOLDLOCK` made every executor's write wait for every other's. Each statement that may
insert one takes `sp_getapplock`, exclusive and owned by the transaction, on the slot's own name and then
reads it under `UPDLOCK`; a refusal raises 51205:

- `start_step` locks `<schema>.step:<job id>:<name>`.
- `access_checkpoint` (Set and GetOrSet), `wait_signal`, `raise_signal`, and `ParkOutboxSignal` lock
  `<schema>.slot:<job id>:<kind>:<name>`.
- `complete_executions` locks the same name for each awaited slot, under both wait kinds, and for each parent latch a child in the batch may raise. It takes them all in one name-ordered pass, after the lanes and before any slot or `jobs_state` row, so no path holds a slot row while it waits for a slot lock.
- `reserve_rate` locks `<schema>.rate:<bucket key>`, so the calls on one meter serialize without a range lock on a bucket not yet stored, whose gap holds the meter's reservations and the next meters.

The sleep timer's slot needs none: `arm_or_consume_sleep_timer` locks the job's `jobs_state` row first.

### Claim watermark

A claim removes each row it takes from `ix_jobs_state_claim_ready`, and the removed entries stay at the
head of the row's band until the database cleans them up. SQL Server leaves ghost records that its
cleanup task removes every few seconds; PostgreSQL marks a dead entry killed only once no transaction
can still see the row. A seek from the head walks every such entry before it reaches a live row, so
claims in a busy namespace spent their time walking, and on PostgreSQL one long transaction anywhere in
the database let the walk grow until it ended.

Each worker therefore starts its claim at a watermark, `p_claim_from_utc`, which bounds each band's seek
from below:

- A claim that comes back full sets the next claim's watermark to its latest due instant less 100 ms.
- A claim that comes back short means the queue is caught up, so the next claim seeks from the head.
- At least once a second a claim seeks from the head anyway.

A row due before the watermark waits for the next claim from the head: a row a claim skipped under
`READPAST` or `SKIP LOCKED`, or one whose enqueue stamped its due instant before a claim and committed
after it. The margin keeps a commit up to 100 ms late within reach. SQLite frees a removed entry at
once, so there the watermark only keeps the claim alike.

### Alert lock order

A statement that writes a job's alerts locks the job row first, then the alert rows. `PurgeJob` holds the
job row while it deletes the job's alerts, and `raise_alert` and `resolve_alerts` lock the same
row before they touch an alert: `UPDLOCK` on SQL Server, `FOR SHARE` on PostgreSQL against the
purge's `FOR UPDATE`. A raise or resolve that meets a purge of its job waits for it. A raise that then
finds the job gone is refused, so no alert outlives its job.

- PostgreSQL takes `FOR SHARE`, not `FOR KEY SHARE`. The key-share path in `heap_lock_tuple` releases
  the buffer lock, relocks it, and then rechecks only for an update. A `FOR UPDATE` taken in that window
  leaves the row only locked, so the key-share joins it instead of waiting. The purge then waits on the
  resolve to delete the job row while the resolve waits on the purge's alert rows. The window opens when
  a raiser's key-share ends under a waiting purge. The share path rechecks for an exclusive lock after
  relocking, and on `jobs` it adds a conflict only with `UpdateJobInput`'s update.

- `ix_alerts_job` (`job_id, alert_id`, filtered to rows with a job) holds a job's alerts. On SQL Server,
  purge and resolve seek it without locks, which the held job row makes safe, and then lock each row by
  primary key. On PostgreSQL and SQLite, their equality on `job_id` seeks it directly. Before that index,
  each SQL Server path scanned `pk_alerts` under update locks, and scans for different jobs deadlocked.
- On SQL Server a row that has a deduplication identity is locked through its
  `ix_alerts_deduplication_key_identity` key before the row itself. `raise_alert` seeks that index under
  `UPDLOCK, HOLDLOCK`, pinned by name, and range-locks the identity and its next key. `PurgeJob` and
  the alert retention sections lock identity rows through the same index before deleting them.
  Retention stages its batch without locks and locks with `READPAST`, so it skips a row a raise holds.
  A delete that took the row first and the index key second deadlocked against a raise, whichever job
  that raise was for.
- Acknowledge and manual resolve lock one alert row by its ref and only read the job, and delivery
  updates are a single-row compare-and-swap by id. None of them waits on a job row while holding an
  alert, so they need no job lock.
- SQLite runs one writer at a time, and each alert write reads its job inside that one transaction.

### Lane lock order

Every routine that touches a laned job locks the `lanes` rows it needs first, in ascending `lanes.lane_id`,
and only then locks `jobs`, `checkpoints`, or `jobs_state` rows, CAS updates included. `jobs_state.lane_id`
never changes after insert, so a routine may read it unlocked to learn which lane to lock. The lane
lock is what serializes a lane: enqueue decides Ready or Blocked under it, every settle to Succeeded,
Failed, or Cancelled promotes the next member under it, and retention deletes a lane that no `jobs_state`
row references under it, skipping a lane another transaction holds and re-checking the reference under
the lock.

- Enqueue resolves the effective lane (the request's, else the definition's) and locks it before the
  parent row, so a parent enqueuing a child into a lane cannot hold the parent while a completing
  sibling in that lane holds the lane and waits for the parent.
- An enqueue batch first inserts the missing lane names, sorted by name, without locking existing
  rows, then locks the whole set in id order. A lane inserted by an uncommitted transaction is
  invisible to every other transaction, so no settle can contend for it.
- The completion batch (`CompleteExecutions`) takes the schedule lock of each namespace it fires a recurring slot in
  first ("Schedule lock"), then locks every lane its rows belong to, in id order, then the awaited signal slots, its own
  `jobs_state` rows in `job_id` order, its fired slots' `schedules` rows and cursors, and last the parents' latches and
  `jobs_state` rows in parent id order, and promotes each settled lane once.
- The upsert retries when retention deleted the lane between the lookup and the lock: the lock finds
  no row, and the next pass inserts the lane again.
- On SQL Server every lane lock is an `UPDLOCK` seek by `lane_id` on the clustered key. A lookup by name
  resolves the id first without a lock, because `ux_lanes_namespace_name` covers `lane_id`, so a lock taken
  through it would land on that index's key alone and never meet a lock taken by id.
- A lane runs at most one member at a time: Ready, Suspended, and Executing are the running statuses. A restarted member is older than the members already waiting, so it can wait Blocked below
  the running one.
- Promotion re-reads under the lock: it takes the lowest-id unfinished member of the lane, promotes it
  when it is Blocked and no member runs, and otherwise leaves it as the head. A single settle ends the
  one running member, so its promotion never meets another; a batch settle also reaches lanes through
  rows it did not settle, so it checks for a running member like every verb. Pausing the running
  member, by the pause verb or by its own handler, promotes the same way: a Paused head holds its lane only against younger
  members, so an older restarted member waiting Blocked below it runs next. Promotion never updates
  through a subselect that a concurrent change could turn into a zero-row update.
- Operator verbs on a laned job (cancel, pause, resume, reschedule, reprioritize, restart, retire)
  lock the lane before the job's row, Blocked followers included. Resume, reschedule, and restart
  choose Blocked when another member runs, or when an older member is unfinished and the job itself
  was not running, and Ready otherwise.
- A definition retire locks the lanes of the definition's parked laned jobs in id order, cancels
  every parked job of the definition, and only then promotes each of those lanes once, so no member of
  the retired definition is promoted on the way. All of it is one transaction.
- `RestartJob` reactivates a finished laned job in place, under the lane lock. It refuses the job
  while an unfinished ancestor or descendant sits in the same lane: the restarted job would run ahead
  of a descendant its handler may wait for, the cycle the enqueue ancestor guard refuses. Both walks
  run under the lane lock, which every enqueue into that lane also takes.
- `reclaim_stuck_jobs` also repairs stranded lanes, whose lowest-id unfinished member is Blocked while
  no member runs.
  Each pass visits at most 1,000 of its namespace's lanes, starting at a random lane id and wrapping,
  seeks each one's head through `ix_jobs_state_lane`, and repairs at most 100 of the stranded lanes it
  finds, in visiting order, so the cost is bounded and stateless and every lane is visited
  eventually. Repair is a safety net for hand edits, so the cap trades a large backlog's drain time
  for a pass that stays short. The walk reads without locks. The pass then locks the stranded lanes
  and the stuck rows' lanes in one id-ordered pass that skips a lane another transaction holds. On SQL
  Server it re-reads the heads under those locks and promotes them in one statement, then writes the
  `job.resumed` events (reason `lane-repaired`) in one more.
- A caller transaction that combines several operations can still take a lane after a row it already
  holds. The database breaks that deadlock, and the aborted owned call is retried like any `40P01` or
  `1205` victim.

### Schedule lock

Each namespace has one schedule lock, held until its transaction ends: `sp_getapplock` on
`{schema}.schedules.{namespace_id}` with a `Transaction` owner on SQL Server, and
`pg_advisory_xact_lock(hashtext('{schema}.schedules'), namespace_id)` or its `_shared` form on PostgreSQL.

- `register_schedules` and `RetireDefinition` take it exclusive.
- A completion that fires a recurring slot and the schedule verbs (pause, resume, overrides, trigger) take it shared, so
  fires and verbs run together and wait only for a registration or a retire. A completion that fires nothing takes none.
- It is a transaction's first lock: a shared holder that took a row first could wait on a registration that waits on
  that row.
- A statement that starts after the grant reads committed schedule config, because a read-committed-snapshot statement
  and a PostgreSQL read-committed statement take their snapshot when they start. The config joins therefore take no row
  locks, and the retirement and orphan filters stay in every reader and writer.
- Rows inside it still lock in order. A verb locks the slot's `jobs_state` row before its `schedules` row, the order a
  fire's completion takes them in. On PostgreSQL, registration locks its slots' `jobs_state` rows in key order, because
  a completion that settles a child locks its slot parent's row without the schedule lock.
- The registration heal clears every orphaned or retired schedule's cursor in the namespace: a retire that commits after
  a worker read its stored schedules leaves that worker's registration writing cursors for the retired definition.
- A worker start skips `register_schedules`, and so the lock, when the namespace stores the start's catalog hash and the
  start's plan moves no stored cursor or slot.
- SQLite runs one writer at a time and takes none of these locks.

## Provisioning, compatibility, and SQL access

Tables, columns, indexes, constraints, durable types, routine bodies, and view bodies all belong in
migrations. A body edit is a migration: `schema add` drafts the changed body into the
next one (see [migrations](../CONTRIBUTING.md#schema-migrations)), and nothing at bootstrap rewrites a body outside a
migration. A bootstrap that finds a newer migration in the database applies nothing, and the
published script refuses such a database. Embedded query changes require no migration.

A routine whose signature changes carries its own cleanup, because `CREATE OR REPLACE` neither
replaces across arities nor changes a return type. A retired arity is dropped after the create; a
changed return type must be dropped before it, and that leading drop names no argument list, because
the routine-body parameter gate reads the file's first parenthesis as the parameter list. The
routine layer therefore reinstalls onto a database from the previous release. Reprovisioning is
required only where a table definition moved, which last happened before 1.0 for the SQL Server
Unicode actor and cannot happen again under the frozen baseline.
From 1.0 onward, an installed routine remains a routine. An inline operation may be promoted
deliberately; routine-to-inline demotion is outside the 1.x policy.

During a rolling deployment all workers share the installed routine body. N+1 provisioning changes
the implementation that N workers invoke, and a rollback never reinstalls N's bodies over N+1's: N
workers keep running on N+1's routines, because a bootstrap applies only the migrations the database
lacks and the published script refuses a database a newer release upgraded.
Changes must therefore preserve the supported parameter, result, and semantic contracts in both
deployment directions. Compatible table DDL alone is insufficient. Body replacement outside a migration
does not remove this executable compatibility obligation.

Dashboard/API are the normal administration surface; human database access can be restricted without
making Acta opaque. Ordinary SELECT inspection and the documented direct SQL enqueue recipes remain
supported, including SSMS, DBeaver, and psql. SQL enqueue becomes visible to worker polling after commit;
it does not itself send an application transport wake-up. See [SQL recipes](operator-guide.md#sql-recipes).
This policy does not declare every internal routine an independently supported operator API or require
an EXECUTE-only runtime account. Installed views remain the curated query surface; storage tables and
custom payload encodings retain their existing compatibility and decoding limits.
